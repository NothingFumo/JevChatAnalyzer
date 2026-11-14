using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JevChatAnalyzer.Core.Db;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.Security;

namespace JevChatAnalyzer.Core.Ledger;

/// <summary>写回状态机：pending=已记账未落库；committed=落库生效；revoked=已撤销；orphaned=对账失败待人工确认。</summary>
public enum WriteState
{
    Pending,
    Committed,
    Revoked,
    Orphaned,
    Rejected
}

/// <summary>一条判读记录（历史上的 HistoryRecord）。</summary>
public sealed record JudgmentRecord
{
    public required string Id { get; init; }

    public required string WxId { get; init; }

    public required string SessionId { get; init; }

    public string SessionTitle { get; init; } = "";

    /// <summary>待分析消息展示文本。</summary>
    public required string FocusText { get; init; }

    /// <summary>待分析消息摘要（server_id 或时间+local_id）。</summary>
    public required string MessageKey { get; init; }

    public required JudgeVerdict Verdict { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
///     一条写回台账记录。它同时承担三件事：写回意图（pending 即待写队列）、
///     目标行原文快照（撤销依据）、以及判读与写回的关联（历史页状态）。
///     原文快照是本应用能把微信库还原到修改之前的唯一依据，因此不再保留整库备份。
/// </summary>
public sealed record WriteBackEntry
{
    public required string Id { get; init; }

    /// <summary>关联的判读记录；历史页用它把「判读」与「写回状态」连起来。</summary>
    public string? JudgmentId { get; init; }

    public required string WxId { get; init; }

    public required string SessionId { get; init; }

    public required string DbFileName { get; init; }

    public required string TableName { get; init; }

    public required string ShardPath { get; init; }

    /// <summary>焦点消息的完整快照（JSON），重放写回时用。</summary>
    public string? FocusJson { get; init; }

    public long? AnnotatedLocalId { get; init; }

    public long? OriginalLocalType { get; init; }

    public bool OriginalContentIsText { get; init; }

    public string? OriginalContentHex { get; init; }

    public long? OriginalCompressType { get; init; }

    public string? JudgmentTableName { get; init; }

    public long? JudgmentLocalId { get; init; }

    public long? JudgmentServerId { get; init; }

    /// <summary>第二次判读时新增的气泡行（local_id）；有值表示本次写回是「新增气泡」而不是改写目标行。</summary>
    public long? InsertedLocalId { get; init; }

    public string? QuoteText { get; init; }

    public string? FocusDisplayText { get; init; }

    public string? FocusSenderWxId { get; init; }

    public WriteState State { get; init; }

    /// <summary>最近一次失败原因（state=pending 时用于诊断）。</summary>
    public string? LastError { get; init; }

    public DateTimeOffset? WrittenAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>历史页视角：一条判读 + 它最近一次写回。</summary>
public sealed record JudgmentView(JudgmentRecord Record, WriteBackEntry? WriteBack);

/// <summary>开始一次写回所需的信息（写库前先记账）。</summary>
public sealed record WriteBackIntent
{
    public string? JudgmentId { get; init; }

    public required string WxId { get; init; }

    public required string SessionId { get; init; }

    public required string ShardPath { get; init; }

    public required string TableName { get; init; }

    public required ChatMessage Focus { get; init; }

    public required string QuoteText { get; init; }

    /// <summary>原文快照；重新写回时沿用上一条台账条目的快照。</summary>
    public long? OriginalLocalType { get; init; }

    public bool OriginalContentIsText { get; init; }

    public string? OriginalContentHex { get; init; }

    public long? OriginalCompressType { get; init; }
}

/// <summary>
///     判读台账：本应用唯一的本地记录库（判读历史 + 写回台账 + 待写队列合并为一个加密 SQLite 库）。
///     单文件 <c>ledger.db</c>，用 SQLCipher 加密，文件密钥经 DPAPI 绑定当前 Windows 用户。
///     所有改动都在事务里完成，不再有「写一半的 JSON」。
/// </summary>
public sealed class LedgerStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dbPath;

    private readonly string _dir;
    private readonly object _gate = new();
    private readonly string _keyPath;
    private SqlCipherNative? _db;

    public LedgerStore(string? appDataDir = null)
    {
        _dir = appDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JevChatAnalyzer");
        _dbPath = Path.Combine(_dir, "ledger.db");
        _keyPath = Path.Combine(_dir, "ledger.key");
    }

    private SqlCipherNative Db
    {
        get
        {
            if (_db is not null) return _db;
            Directory.CreateDirectory(_dir);
            var fresh = !File.Exists(_dbPath);
            try
            {
                _db = SqlCipherNative.OpenReadWriteCreate(_dbPath, GetKeyHex());
            }
            catch (SqliteNativeException) when (File.Exists(_dbPath))
            {
                // 密钥不匹配或文件损坏：保留现场后重建，绝不静默覆盖
                var quarantine = Path.Combine(_dir,
                    $"ledger.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.db");
                File.Move(_dbPath, quarantine, false);
                _db = SqlCipherNative.OpenReadWriteCreate(_dbPath, GetKeyHex());
            }

            EnsureSchema(_db);
            if (fresh) DeleteLegacyArtifacts();
            return _db;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db?.Dispose();
            _db = null;
        }
    }

    /// <summary>文件密钥（32 字节 hex），经 DPAPI 保护；换用户/损坏则重建。</summary>
    private string GetKeyHex()
    {
        Directory.CreateDirectory(_dir);
        if (File.Exists(_keyPath))
            try
            {
                var key = ProtectedStore.Unprotect(File.ReadAllText(_keyPath));
                if (key.Length == 32) return Convert.ToHexStringLower(key);
            }
            catch (Exception e) when (e is CryptographicException or FormatException or IOException)
            {
                // 换用户 / 密钥文件被写坏（Base64 非法）：旧库已无法解密，交由 Open 的损坏分支改名保留。
                // 不捕获的话 FormatException 会让台账每次启动都抛，连撤销都做不了。
            }

        var fresh = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(_keyPath, ProtectedStore.ProtectBytes(fresh));
        return Convert.ToHexStringLower(fresh);
    }

    /// <summary>
    ///     台账首次创建时清掉上一代的明文/半明文文件（history.bin、history.key、backups/index.json、
    ///     backups/pending.json）。它们已被判读台账取代，且含对方原话与原文 hex，留着只有泄露风险。
    ///     微信库的 .bak 快照不动（那是用户自己的数据）。
    /// </summary>
    private void DeleteLegacyArtifacts()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(_dir, "history.bin"),
                     Path.Combine(_dir, "history.key"),
                     Path.Combine(_dir, "backups", "index.json"),
                     Path.Combine(_dir, "backups", "pending.json")
                 })
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // 被占用就下次再清，不影响台账使用
            }
            catch (UnauthorizedAccessException)
            {
                // 同上
            }
    }

    private static void EnsureSchema(SqlCipherNative db)
    {
        db.Execute("PRAGMA journal_mode=WAL");
        db.Execute("""
                   CREATE TABLE IF NOT EXISTS judgment (
                       id TEXT PRIMARY KEY,
                       wx_id TEXT NOT NULL,
                       session_id TEXT NOT NULL,
                       session_title TEXT NOT NULL DEFAULT '',
                       focus_text TEXT NOT NULL,
                       message_key TEXT NOT NULL,
                       verdict_json TEXT NOT NULL,
                       created_at INTEGER NOT NULL
                   )
                   """);
        db.Execute("CREATE INDEX IF NOT EXISTS ix_judgment_created ON judgment(created_at)");
        db.Execute("""
                   CREATE TABLE IF NOT EXISTS writeback (
                       id TEXT PRIMARY KEY,
                       judgment_id TEXT,
                       wx_id TEXT NOT NULL,
                       session_id TEXT NOT NULL,
                       db_file TEXT NOT NULL,
                       table_name TEXT NOT NULL,
                       shard_path TEXT NOT NULL,
                       focus_json TEXT,
                       annotated_local_id INTEGER,
                       original_local_type INTEGER,
                       original_content_is_text INTEGER NOT NULL DEFAULT 1,
                       original_content_hex TEXT,
                       original_compress_type INTEGER,
                       judgment_table TEXT,
                       judgment_local_id INTEGER,
                       judgment_server_id INTEGER,
                       quote_text TEXT,
                       focus_display_text TEXT,
                       focus_sender_wx_id TEXT,
                       state TEXT NOT NULL,
                       last_error TEXT,
                       written_at INTEGER,
                       revoked_at INTEGER,
                       inserted_local_id INTEGER
                   )
                   """);
        if (!db.Query("PRAGMA table_info(writeback)")
                .Any(r => string.Equals(r.Text("name"), "inserted_local_id", StringComparison.OrdinalIgnoreCase)))
            // 旧库补列：已有写入只改目标行，inserted_local_id 为空即旧语义
            db.Execute("ALTER TABLE writeback ADD COLUMN inserted_local_id INTEGER");
        db.Execute("CREATE INDEX IF NOT EXISTS ix_writeback_state ON writeback(state)");
        db.Execute("CREATE INDEX IF NOT EXISTS ix_writeback_local ON writeback(session_id, annotated_local_id)");
    }

    // ── 判读记录 ─────────────────────────────────────────────────────

    public void AddJudgment(JudgmentRecord record)
    {
        lock (_gate)
        {
            var db = Db;
            db.Execute("BEGIN");
            try
            {
                db.Execute($"""
                            INSERT OR REPLACE INTO judgment
                            (id, wx_id, session_id, session_title, focus_text, message_key, verdict_json, created_at)
                            VALUES ('{Esc(record.Id)}', '{Esc(record.WxId)}', '{Esc(record.SessionId)}',
                                    '{Esc(record.SessionTitle)}', '{Esc(record.FocusText)}', '{Esc(record.MessageKey)}',
                                    '{Esc(JsonSerializer.Serialize(record.Verdict, JsonOptions))}',
                                    {record.CreatedAt.ToUnixTimeMilliseconds()})
                            """);
                db.Execute("COMMIT");
            }
            catch
            {
                TryRollback(db);
                throw;
            }
        }
    }

    public IReadOnlyList<JudgmentView> Recent(int count = 200, string? wxId = null)
    {
        lock (_gate)
        {
            var db = Db;
            var filter = wxId is null ? "" : $" WHERE wx_id = '{Esc(wxId)}'";
            var rows = db.Query($"""
                                 SELECT * FROM judgment{filter}
                                 ORDER BY created_at DESC LIMIT {Math.Max(1, count)}
                                 """);
            var records = rows.Select(ReadJudgment).ToList();
            if (records.Count == 0) return [];
            var ids = string.Join(",", records.Select(r => $"'{Esc(r.Id)}'"));
            var writes = db.Query($"""
                                   SELECT * FROM writeback WHERE judgment_id IN ({ids})
                                   ORDER BY COALESCE(written_at, 0) DESC
                                   """).Select(ReadWriteBack).ToList();
            return records
                .Select(r => new JudgmentView(r, writes.FirstOrDefault(w => w.JudgmentId == r.Id)))
                .ToList();
        }
    }

    public void DeleteJudgment(string id)
    {
        lock (_gate)
        {
            var db = Db;
            var active = db.Query($"""
                                   SELECT COUNT(*) AS n FROM writeback
                                   WHERE judgment_id = '{Esc(id)}' AND state = 'committed'
                                   """)[0].Int("n");
            if (active > 0)
                throw new InvalidOperationException(
                    "这条判读的写回还在生效中。撤销写回后才能删除记录（记录是还原微信库的唯一依据）。");
            db.Execute("BEGIN");
            try
            {
                db.Execute($"DELETE FROM writeback WHERE judgment_id = '{Esc(id)}'");
                db.Execute($"DELETE FROM judgment WHERE id = '{Esc(id)}'");
                db.Execute("COMMIT");
            }
            catch
            {
                TryRollback(db);
                throw;
            }
        }
    }

    // ── 写回台账 ─────────────────────────────────────────────────────

    /// <summary>先记账：写入 pending 条目（含原文快照），随后才允许碰微信库。</summary>
    internal WriteBackEntry BeginWriteBack(WriteBackIntent intent)
    {
        lock (_gate)
        {
            var db = Db;
            var id = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            db.Execute("BEGIN");
            try
            {
                db.Execute($"""
                            INSERT INTO writeback
                            (id, judgment_id, wx_id, session_id, db_file, table_name, shard_path, focus_json,
                             annotated_local_id, original_local_type, original_content_is_text,
                             original_content_hex, original_compress_type, quote_text,
                             focus_display_text, focus_sender_wx_id, state, written_at)
                            VALUES ('{Esc(id)}', {NullableText(intent.JudgmentId)}, '{Esc(intent.WxId)}',
                                    '{Esc(intent.SessionId)}', '{Esc(Path.GetFileName(intent.ShardPath))}',
                                    '{Esc(intent.TableName)}', '{Esc(intent.ShardPath)}',
                                    '{Esc(JsonSerializer.Serialize(intent.Focus, JsonOptions))}',
                                    {intent.Focus.LocalId}, {NullableLong(intent.OriginalLocalType)},
                                    {(intent.OriginalContentIsText ? 1 : 0)},
                                    {NullableText(intent.OriginalContentHex)},
                                    {NullableLong(intent.OriginalCompressType)},
                                    '{Esc(intent.QuoteText)}',
                                    {NullableText(intent.Focus.DisplayText)},
                                    {NullableText(intent.Focus.SenderWxId)},
                                    'pending', {now})
                            """);
                db.Execute("COMMIT");
            }
            catch
            {
                TryRollback(db);
                throw;
            }

            return GetWriteBack(id)!;
        }
    }

    /// <summary>落库前把目标行原文写进台账（还原依据）。</summary>
    internal void SaveSnapshot(string id, long originalLocalType, bool originalContentIsText,
        string originalContentHex, long? originalCompressType)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        UPDATE writeback
                        SET original_local_type = {originalLocalType},
                            original_content_is_text = {(originalContentIsText ? 1 : 0)},
                            original_content_hex = '{Esc(originalContentHex)}',
                            original_compress_type = {NullableLong(originalCompressType)}
                        WHERE id = '{Esc(id)}'
                        """);
            if (Db.LastChanges() == 0) throw new InvalidOperationException("待写台账条目已消失，拒绝在无原文快照的情况下改库。");
        }
    }

    /// <summary>同一条消息只保留一条待写条目（保留 exceptId，通常是刚插入的新条目）。</summary>
    internal void DeletePendingFor(string sessionId, long localId, string? exceptId = null)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        DELETE FROM writeback
                        WHERE state = 'pending' AND session_id = '{Esc(sessionId)}' AND annotated_local_id = {localId}
                          {(exceptId is null ? "" : $"AND id <> '{Esc(exceptId)}'")}
                        """);
        }
    }

    /// <summary>
    ///     这条消息最近一次的原文快照（任意状态）。pending 也可能表示「库已改、台账没来得及更新」，
    ///     因此在重新入队前必须先把快照转移到新条目，否则删掉旧条目等于永久丢掉还原依据。
    /// </summary>
    internal WriteBackEntry? LatestSnapshotFor(string sessionId, long localId)
    {
        lock (_gate)
        {
            var rows = Db.Query($"""
                                 SELECT * FROM writeback
                                 WHERE session_id = '{Esc(sessionId)}' AND annotated_local_id = {localId}
                                   AND original_content_hex IS NOT NULL
                                 ORDER BY written_at DESC LIMIT 1
                                 """);
            return rows.Count == 0 ? null : ReadWriteBack(rows[0]);
        }
    }

    internal void CompleteWriteBack(string id, long judgmentLocalId, long judgmentServerId, string judgmentTable)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        UPDATE writeback
                        SET state = 'committed',
                            judgment_table = '{Esc(judgmentTable)}',
                            judgment_local_id = {judgmentLocalId},
                            judgment_server_id = {judgmentServerId},
                            written_at = {DateTimeOffset.Now.ToUnixTimeMilliseconds()},
                            last_error = NULL
                        WHERE id = '{Esc(id)}'
                        """);
        }
    }

    /// <summary>同一行被重新写回：把旧的生效条目降级为「已被取代」，保证每条消息只有一条生效写回。</summary>
    internal void SupersedeOtherCommitted(string sessionId, long localId, string keepId)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        UPDATE writeback
                        SET state = 'revoked',
                            revoked_at = {DateTimeOffset.Now.ToUnixTimeMilliseconds()},
                            last_error = '已被新的写回取代'
                        WHERE session_id = '{Esc(sessionId)}' AND annotated_local_id = {localId}
                          AND state = 'committed' AND id <> '{Esc(keepId)}'
                        """);
        }
    }

    /// <summary>落库失败：保留 pending 供退避重试，同时记下原因。</summary>
    internal void FailWriteBack(string id, string error)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        UPDATE writeback SET last_error = '{Esc(error)}' WHERE id = '{Esc(id)}'
                        """);
        }
    }

    /// <summary>结构性失败（不是文本消息 / 找不到目标行 / 缺少原文快照）：撤掉待写条目，不重试。</summary>
    internal void AbandonPending(string id, string reason)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        DELETE FROM writeback WHERE id = '{Esc(id)}' AND state = 'pending'
                        """);
            _ = reason;
        }
    }

    internal void MarkOrphaned(string id, string reason)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        UPDATE writeback
                        SET state = 'orphaned', last_error = '{Esc(reason)}',
                            written_at = COALESCE(written_at, {DateTimeOffset.Now.ToUnixTimeMilliseconds()})
                        WHERE id = '{Esc(id)}' AND state <> 'revoked'
                        """);
        }
    }

    /// <summary>记录本次写回新增的气泡行（撤销按它精确删除）。</summary>
    internal void SetInsertedRow(string id, long insertedLocalId)
    {
        lock (_gate)
        {
            Db.Execute($"UPDATE writeback SET inserted_local_id = {insertedLocalId} WHERE id = '{Esc(id)}'");
        }
    }

    /// <summary>确定性失败（目标不是文本/引用回复、快照缺失等）：标记后不再重试。</summary>
    internal void MarkRejected(string id, string reason)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        UPDATE writeback
                        SET state = 'rejected', last_error = '{Esc(reason)}'
                        WHERE id = '{Esc(id)}' AND state = 'pending'
                        """);
        }
    }

    internal void MarkRevoked(string id)
    {
        lock (_gate)
        {
            Db.Execute($"""
                        UPDATE writeback
                        SET state = 'revoked', revoked_at = {DateTimeOffset.Now.ToUnixTimeMilliseconds()},
                            last_error = NULL
                        WHERE id = '{Esc(id)}'
                        """);
        }
    }

    internal WriteBackEntry? GetWriteBack(string id)
    {
        lock (_gate)
        {
            var rows = Db.Query($"SELECT * FROM writeback WHERE id = '{Esc(id)}'");
            return rows.Count == 0 ? null : ReadWriteBack(rows[0]);
        }
    }

    /// <summary>某条消息当前生效中的写回（重新写回时用于沿用原文快照）。</summary>
    internal WriteBackEntry? ActiveWriteBack(string sessionId, long localId)
    {
        lock (_gate)
        {
            var rows = Db.Query($"""
                                 SELECT * FROM writeback
                                 WHERE session_id = '{Esc(sessionId)}' AND annotated_local_id = {localId}
                                   AND state = 'committed'
                                 ORDER BY written_at DESC LIMIT 1
                                 """);
            return rows.Count == 0 ? null : ReadWriteBack(rows[0]);
        }
    }

    public IReadOnlyList<WriteBackEntry> ListByState(WriteState state)
    {
        lock (_gate)
        {
            return Db.Query($"""
                             SELECT * FROM writeback WHERE state = '{state.ToString().ToLowerInvariant()}'
                             ORDER BY COALESCE(written_at, 0) DESC
                             """).Select(ReadWriteBack).ToList();
        }
    }

    public IReadOnlyList<WriteBackEntry> RecentWriteBacks(int count = 200)
    {
        lock (_gate)
        {
            return Db.Query($"""
                             SELECT * FROM writeback ORDER BY COALESCE(written_at, 0) DESC LIMIT {Math.Max(1, count)}
                             """).Select(ReadWriteBack).ToList();
        }
    }

    /// <summary>待写队列条数（监视器每轮判空用）。</summary>
    public int PendingCount()
    {
        lock (_gate)
        {
            return (int)Db.Query("SELECT COUNT(*) AS n FROM writeback WHERE state = 'pending'")[0].Int("n");
        }
    }

    // ── 行映射 ───────────────────────────────────────────────────────

    private static JudgmentRecord ReadJudgment(SqlCipherNative.Row row)
    {
        return new JudgmentRecord
        {
            Id = row.Text("id") ?? "",
            WxId = row.Text("wx_id") ?? "",
            SessionId = row.Text("session_id") ?? "",
            SessionTitle = row.Text("session_title") ?? "",
            FocusText = row.Text("focus_text") ?? "",
            MessageKey = row.Text("message_key") ?? "",
            Verdict = JsonSerializer.Deserialize<JudgeVerdict>(row.Text("verdict_json") ?? "{}", JsonOptions)
                      ?? throw new InvalidOperationException("判读记录损坏：verdict_json 无法解析。"),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.Int("created_at"))
        };
    }

    private static WriteBackEntry ReadWriteBack(SqlCipherNative.Row row)
    {
        return new WriteBackEntry
        {
            Id = row.Text("id") ?? "",
            JudgmentId = row.Text("judgment_id"),
            WxId = row.Text("wx_id") ?? "",
            SessionId = row.Text("session_id") ?? "",
            DbFileName = row.Text("db_file") ?? "",
            TableName = row.Text("table_name") ?? "",
            ShardPath = row.Text("shard_path") ?? "",
            FocusJson = row.Text("focus_json"),
            AnnotatedLocalId = row.IntOrNull("annotated_local_id"),
            OriginalLocalType = row.IntOrNull("original_local_type"),
            OriginalContentIsText = row.IntOrNull("original_content_is_text") is not 0,
            OriginalContentHex = row.Text("original_content_hex"),
            OriginalCompressType = row.IntOrNull("original_compress_type"),
            JudgmentTableName = row.Text("judgment_table"),
            JudgmentLocalId = row.IntOrNull("judgment_local_id"),
            JudgmentServerId = row.IntOrNull("judgment_server_id"),
            InsertedLocalId = row.IntOrNull("inserted_local_id"),
            QuoteText = row.Text("quote_text"),
            FocusDisplayText = row.Text("focus_display_text"),
            FocusSenderWxId = row.Text("focus_sender_wx_id"),
            State = Enum.TryParse<WriteState>(row.Text("state"), true, out var st) ? st : WriteState.Pending,
            LastError = row.Text("last_error"),
            WrittenAt = row.IntOrNull("written_at") is { } w ? DateTimeOffset.FromUnixTimeMilliseconds(w) : null,
            RevokedAt = row.IntOrNull("revoked_at") is { } r ? DateTimeOffset.FromUnixTimeMilliseconds(r) : null
        };
    }

    private static void TryRollback(SqlCipherNative db)
    {
        try
        {
            db.Execute("ROLLBACK");
        }
        catch
        {
            /* 事务已结束则忽略 */
        }
    }

    private static string Esc(string s)
    {
        return s.Replace("'", "''");
    }

    private static string NullableText(string? s)
    {
        return s is null ? "NULL" : $"'{Esc(s)}'";
    }

    private static string NullableLong(long? v)
    {
        return v?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
    }
}
