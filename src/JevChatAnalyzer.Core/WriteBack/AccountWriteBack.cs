using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JevChatAnalyzer.Core.Db;
using JevChatAnalyzer.Core.Keys;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;
using ZstdSharp;

namespace JevChatAnalyzer.Core.WriteBack;

/// <summary>写回失败（消息面向用户，中文）。可重试：条目留在待写队列。</summary>
public class WriteBackException : Exception
{
    public WriteBackException(string message) : base(message)
    {
    }
}

/// <summary>写回被拒绝（结构性原因，重试也不会成功）：待写条目直接撤销。</summary>
public sealed class WriteBackRejected : WriteBackException
{
    public WriteBackRejected(string message) : base(message)
    {
    }
}

/// <summary>目标消息行在写回前的样子（还原依据）。</summary>
internal sealed record SnapshotRow(long LocalType, bool IsText, byte[] Bytes, long? CompressType, string Text);

/// <summary>
///     写回服务：把判读结论写进「对方那条消息」的引用块，并可逐行还原。
///     自本版本起：不再做整库备份。还原依据是判读台账里的原文快照（local_type + 原始字节 + 压缩类型），
///     因此还原是行级 SQL 回滚，而不是文件覆盖。
///     时序（先记账后改库）：
///     ① 台账写入 pending（含目标行定位）→ ② 读目标行、快照入台账
///     → ③ 微信库单事务：插入判读行 + UPDATE 目标行 → ④ 台账置 committed。
///     进程在 ③ 前后崩溃都可由 <see cref="Reconcile" /> 对账：行已是引用块 → 补 committed；仍是原文 → 保留 pending 重试。
/// </summary>
public sealed class AccountWriteBack
{
    /// <summary>
    ///     判读行放在这个「伪会话」对应的表里：它不对应任何真实会话，微信永远不会打开它，
    ///     因此不会渲染出气泡；而引用块按 fromusr + svrid 仍能解析到判读文本。
    /// </summary>
    public const string JudgmentSessionId = "jca_judgment_quote@chatroom";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dbKeyHex;

    /// <summary>绑定一个账号：库坐标（密钥）只在这里出现，调用方只交台账条目。</summary>
    public AccountWriteBack(LedgerStore ledger, string dbKeyHex)
    {
        Ledger = ledger;
        _dbKeyHex = dbKeyHex;
    }

    private static string JudgmentTable => MessageTable.For(JudgmentSessionId);

    /// <summary>伪会话表名（测试用）。</summary>
    internal static string JudgmentTableForTest => JudgmentTable;

    public LedgerStore Ledger { get; }

    // ── 队列 ─────────────────────────────────────────────────────────

    /// <summary>入队一条待写回（幂等：同一条消息只保留最新结论）。只记账，不碰微信库。</summary>
    public WriteBackEntry EnqueuePending(string wxId, string sessionId, ChatMessage focusMessage,
        string quoteText, string? judgmentId = null)
    {
        if (focusMessage.IsMine) throw new WriteBackException("只能对「对方发送的消息」自动写回。");
        // 先把旧条目的原文快照转移到新条目，再删旧条目：顺序反过来会在崩溃时永久丢掉还原依据
        var intent = WithCarriedSnapshot(Intent(wxId, sessionId, focusMessage, quoteText, judgmentId), sessionId,
            focusMessage.LocalId);
        var entry = Ledger.BeginWriteBack(intent);
        Ledger.DeletePendingFor(sessionId, focusMessage.LocalId, entry.Id);
        return entry;
    }

    /// <summary>落库全部待写项（微信运行中同样直写）。返回每个条目的结果。</summary>
    public IReadOnlyList<(WriteBackEntry? Entry, string? Error)> FlushPending()
    {
        var results = new List<(WriteBackEntry? Entry, string? Error)>();
        foreach (var pending in Ledger.ListByState(WriteState.Pending))
            try
            {
                results.Add((Commit(pending), null));
            }
            catch (WriteBackRejected e)
            {
                // 目标类型不合法、快照缺失等确定性错误：重试不会成功，标记后移出待写队列。
                Ledger.MarkRejected(pending.Id, e.Message);
                results.Add((pending, $"{e.Message}（已标记为无法写回，不再重试）"));
            }
            catch (Exception e)
            {
                // 瞬时失败（微信占用、文件锁等）保留在队列中，下一轮重试
                results.Add((pending, e.Message));
            }

        return results;
    }

    /// <summary>直接写回（入队 + 落库一步到位），返回 committed 台账条目。</summary>
    internal WriteBackEntry AnnotateMessage(string dbShardPath, string tableName,
        string wxId, string sessionId, ChatMessage focusMessage, string quoteText, string? judgmentId = null)
    {
        if (focusMessage.IsMine) throw new WriteBackException("只能给「对方发送的消息」加引用块（本软件的分析对象）。");
        var intent = WithCarriedSnapshot(Intent(wxId, sessionId, focusMessage, quoteText, judgmentId), sessionId,
                focusMessage.LocalId)
            with
            {
                ShardPath = dbShardPath, TableName = tableName
            };
        var pending = Ledger.BeginWriteBack(intent);
        Ledger.DeletePendingFor(sessionId, focusMessage.LocalId, pending.Id);
        return Commit(pending);
    }

    /// <summary>按台账把旧判读原样重放（撤销之后用）：不重新判读，只重建引用块。</summary>
    public WriteBackEntry Reapply(WriteBackEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.QuoteText) || entry.FocusJson is null)
            throw new WriteBackException("该台账条目没有可重放的判读结果。");
        var focus = JsonSerializer.Deserialize<ChatMessage>(entry.FocusJson, JsonOptions)
                    ?? throw new WriteBackException("台账里的焦点消息快照损坏，无法重放。");
        var pending = Ledger.BeginWriteBack(new WriteBackIntent
        {
            JudgmentId = entry.JudgmentId,
            WxId = entry.WxId,
            SessionId = entry.SessionId,
            ShardPath = entry.ShardPath,
            TableName = entry.TableName,
            Focus = focus,
            QuoteText = entry.QuoteText,
            // 沿用最初那次写回保存的原文快照
            OriginalLocalType = entry.OriginalLocalType,
            OriginalContentIsText = entry.OriginalContentIsText,
            OriginalContentHex = entry.OriginalContentHex,
            OriginalCompressType = entry.OriginalCompressType
        });
        return Commit(pending);
    }

    // ── 撤销 / 还原 ──────────────────────────────────────────────────

    /// <summary>
    ///     撤销一次写回：把该行还原为改写前的 local_type 与 message_content，并删掉判读行。
    ///     走行级 SQL，微信运行中同样可行（实时生效需重开会话）。
    /// </summary>
    public int Revoke(WriteBackEntry entry)
    {
        if (entry.InsertedLocalId is { } insertedLocalId) return RevokeAddedBubble(entry, insertedLocalId);
        if (entry.State == WriteState.Revoked && entry.RevokedAt is not null)
            throw new WriteBackException("该条目已撤销，无需再次还原。");
        if (entry.AnnotatedLocalId is not { } localId ||
            entry.OriginalLocalType is not { } originalType ||
            entry.OriginalContentHex is not { } originalHex)
            throw new WriteBackException("台账里缺少该条目的原文快照，无法按行还原。");
        var shard = ResolveShard(entry);

        var changed = 0;
        using (var db = SqlCipherNative.OpenReadWrite(shard, _dbKeyHex))
        {
            // 还原前确认该行仍是本工具写的引用块：行被删、被替换或被微信改写时，
            // 盲目 UPDATE 会写坏数据，也可能「报告成功但什么都没做」。
            SnapshotRow current;
            try
            {
                current = ReadRow(db, entry.TableName, localId);
            }
            catch (WriteBackRejected e)
            {
                Ledger.MarkOrphaned(entry.Id, e.Message);
                throw new WriteBackException($"无法还原：{e.Message}");
            }

            if (!IsOurs(current))
            {
                Ledger.MarkOrphaned(entry.Id, "目标行已不是本工具写入的引用块");
                throw new WriteBackException(
                    "无法还原：这条消息已不是本工具写入的引用块（可能被删除、被替换或被微信改写）。台账已标记为「待确认」。");
            }

            var hasCtColumn = db.Query($"PRAGMA table_info(\"{Esc(entry.TableName)}\")")
                .Any(r => string.Equals(r.Text("name"), "WCDB_CT_message_content", StringComparison.OrdinalIgnoreCase));
            db.Execute("BEGIN");
            try
            {
                var assignment = entry.OriginalContentIsText
                    ? $"'{Esc(Encoding.UTF8.GetString(Convert.FromHexString(originalHex)))}'"
                    : $"X'{originalHex}'";
                var compressType = entry.OriginalCompressType?.ToString() ?? "NULL";
                db.Execute($"""
                            UPDATE "{Esc(entry.TableName)}"
                            SET local_type = {originalType},
                                message_content = {assignment}
                                {(hasCtColumn ? $", WCDB_CT_message_content = {compressType}" : "")}
                            WHERE local_id = {localId}
                            """);
                changed = db.LastChanges();
                if (changed == 0) throw new WriteBackRejected($"目标行在还原过程中消失（{entry.TableName}#{localId}）");

                if (entry.JudgmentTableName is { } judgmentTable)
                {
                    var judgmentLocalId = entry.JudgmentLocalId;
                    if (judgmentLocalId is null && entry.JudgmentServerId is { } serverId)
                        judgmentLocalId = db.Query(
                                $"SELECT local_id FROM \"{Esc(judgmentTable)}\" WHERE server_id = {serverId}")
                            .FirstOrDefault()?.Int("local_id");
                    if (judgmentLocalId is { } id)
                        db.Execute($"DELETE FROM \"{Esc(judgmentTable)}\" WHERE local_id = {id}");
                }

                db.Execute("COMMIT");
            }
            catch (WriteBackRejected e)
            {
                try
                {
                    db.Execute("ROLLBACK");
                }
                catch
                {
                    /* 尽力回滚 */
                }

                Ledger.MarkOrphaned(entry.Id, e.Message);
                throw new WriteBackException($"无法还原：{e.Message}");
            }
            catch
            {
                try
                {
                    db.Execute("ROLLBACK");
                }
                catch
                {
                    /* 尽力回滚 */
                }

                throw;
            }

            if (entry.JudgmentTableName is { } table)
            {
                var left = db.Query($"SELECT COUNT(*) AS n FROM \"{Esc(table)}\" ")[0].Int("n");
                if (left == 0) db.Execute($"DROP TABLE \"{Esc(table)}\"");
            }
        }

        Ledger.MarkRevoked(entry.Id);
        return changed;
    }

    /// <summary>撤销「新增气泡」：删除气泡行与判读行，目标消息原样保留。</summary>
    private int RevokeAddedBubble(WriteBackEntry entry, long insertedLocalId)
    {
        if (entry.State == WriteState.Revoked && entry.RevokedAt is not null)
            throw new WriteBackException("该条目已撤销，无需再次还原。");
        var shard = ResolveShard(entry);
        using (var db = SqlCipherNative.OpenReadWrite(shard, _dbKeyHex))
        {
            db.Execute("BEGIN");
            try
            {
                db.Execute($"""
                            DELETE FROM "{Esc(entry.TableName)}"
                            WHERE local_id = {insertedLocalId} AND IFNULL(source,'') = '{ChatStore.AnnotationBubbleSource}'
                            """);
                if (db.LastChanges() == 0) throw new WriteBackRejected("新增气泡已不存在（可能被删除或被改写）");
                if (entry.JudgmentTableName is { } table && entry.JudgmentLocalId is { } judgmentId)
                    db.Execute($"DELETE FROM \"{Esc(table)}\" WHERE local_id = {judgmentId}");
                db.Execute("COMMIT");
            }
            catch (WriteBackRejected e)
            {
                try
                {
                    db.Execute("ROLLBACK");
                }
                catch
                {
                    /* 事务已结束则忽略 */
                }

                Ledger.MarkOrphaned(entry.Id, e.Message);
                throw new WriteBackException($"无法还原：{e.Message}");
            }
            catch
            {
                try
                {
                    db.Execute("ROLLBACK");
                }
                catch
                {
                    /* 事务已结束则忽略 */
                }

                throw;
            }

            if (entry.JudgmentTableName is { } judgmentTable)
            {
                var left = db.Query($"SELECT COUNT(*) AS n FROM \"{Esc(judgmentTable)}\"")[0].Int("n");
                if (left == 0) db.Execute($"DROP TABLE \"{Esc(judgmentTable)}\"");
            }
        }

        Ledger.MarkRevoked(entry.Id);
        return 1;
    }

    /// <summary>
    ///     还原到本应用修改之前：按台账把每一条生效中的写回逐条回滚（行级 SQL）。
    ///     单条失败不中断其余条目，逐条返回结果，避免「部分还原却报告成功」。
    /// </summary>
    public IReadOnlyList<(WriteBackEntry Entry, string? Error)> RestoreAll()
    {
        var results = new List<(WriteBackEntry, string?)>();
        foreach (var entry in Ledger.ListByState(WriteState.Committed))
            try
            {
                Revoke(entry);
                results.Add((entry, null));
            }
            catch (Exception e)
            {
                results.Add((entry, e.Message));
            }

        return results;
    }

    /// <summary>分片被移动时按「账号目录 / 文件名」重新定位（旧路径失效不应让还原永久失败）。</summary>
    private static string ResolveShard(WriteBackEntry entry)
    {
        var direct = entry.ShardPath;
        if (!string.IsNullOrWhiteSpace(direct) && File.Exists(direct)) return direct;
        var locator = new WeChatDataLocator();
        foreach (var (dir, wxId) in locator.ListAccounts())
        {
            if (!string.Equals(wxId, entry.WxId, StringComparison.Ordinal)) continue;
            var candidate = Path.Combine(WeChatDataLocator.MessageDbDir(dir), entry.DbFileName);
            if (File.Exists(candidate)) return candidate;
        }

        throw new WriteBackException(
            $"找不到目标分片库 {entry.DbFileName}。账号目录可能已移动，还原无从下手。");
    }

    // ── 对账 ─────────────────────────────────────────────────────────

    /// <summary>
    ///     启动对账：把停在 pending 的条目与微信库实际状态对齐。
    ///     行已是本工具的引用块 → 补 committed；仍是原文 → 保留 pending 供重试；库/表不可读 → orphaned。
    /// </summary>
    public IReadOnlyList<(WriteBackEntry Entry, string Outcome)> Reconcile()
    {
        var results = new List<(WriteBackEntry, string)>();
        foreach (var pending in Ledger.ListByState(WriteState.Pending))
        {
            if (pending.AnnotatedLocalId is not { } localId)
            {
                Ledger.MarkOrphaned(pending.Id, "台账条目缺少目标行定位");
                results.Add((pending, "orphaned"));
                continue;
            }

            string shard;
            try
            {
                // 账号目录整体搬迁后旧路径失效：按「账号目录 + 文件名」重定位，而不是直接判死
                shard = ResolveShard(pending);
            }
            catch (WriteBackException e)
            {
                Ledger.MarkOrphaned(pending.Id, e.Message);
                results.Add((pending, "orphaned"));
                continue;
            }

            try
            {
                using var db = SqlCipherNative.OpenReadOnly(shard, _dbKeyHex);
                if (pending.InsertedLocalId is { } bubbleId)
                {
                    // 新增气泡条目：行在 → committed；行没了 → 保持 pending 由用户重试或撤销
                    var present = db.Query($"""
                                            SELECT COUNT(*) AS n FROM "{Esc(pending.TableName)}"
                                            WHERE local_id = {bubbleId} AND IFNULL(source,'') = '{ChatStore.AnnotationBubbleSource}'
                                            """)[0].Int("n");
                    if (present == 0)
                    {
                        results.Add((pending, "pending"));
                        continue;
                    }

                    Ledger.CompleteWriteBack(pending.Id, pending.JudgmentLocalId ?? 0,
                        pending.JudgmentServerId ?? 0, pending.JudgmentTableName ?? JudgmentTable);
                    results.Add((pending, "committed"));
                    continue;
                }

                var row = ReadRow(db, pending.TableName, localId);
                if (!IsOurs(row))
                {
                    results.Add((pending, "pending"));
                    continue;
                }

                var serverId = QuoteMessage.Parse(row.Text)?.Quote?.RefServerId;
                long? judgmentLocalId = null;
                if (serverId is { } svr)
                    judgmentLocalId = db.Query(
                            $"SELECT local_id FROM \"{JudgmentTable}\" WHERE server_id = {svr}")
                        .FirstOrDefault()?.Int("local_id");
                Ledger.CompleteWriteBack(pending.Id, judgmentLocalId ?? 0, serverId ?? 0, JudgmentTable);
                results.Add((pending, "committed"));
            }
            catch (WriteBackRejected e)
            {
                Ledger.MarkOrphaned(pending.Id, e.Message);
                results.Add((pending, "orphaned"));
            }
            catch (Exception)
            {
                // 锁冲突 / IO 抖动等瞬时错误：保持 pending，下一轮再对账，绝不据此把条目判死
                results.Add((pending, "pending"));
            }
        }

        return results;
    }

    // ── 落库 ─────────────────────────────────────────────────────────

    /// <summary>把一条 pending 条目真正写进微信库（单事务：判读行 + 目标行）。</summary>
    private WriteBackEntry Commit(WriteBackEntry pending)
    {
        try
        {
            return CommitCore(pending);
        }
        catch (WriteBackRejected e)
        {
            // 结构性失败：重试无意义，撤掉待写条目（库没有被改动）
            Ledger.AbandonPending(pending.Id, e.Message);
            throw;
        }
        catch (Exception e)
        {
            // 传输/锁/事务类失败：保留 pending 供退避重试
            Ledger.FailWriteBack(pending.Id, e.Message);
            throw;
        }
    }

    private WriteBackEntry CommitCore(WriteBackEntry pending)
    {
        if (pending.AnnotatedLocalId is not { } localId) throw new WriteBackException("台账条目缺少目标行定位。");
        if (!File.Exists(pending.ShardPath)) throw new WriteBackException($"找不到目标分片库 {pending.DbFileName}。账号目录可能已移动。");
        var focus = pending.FocusJson is null
            ? null
            : JsonSerializer.Deserialize<ChatMessage>(pending.FocusJson, JsonOptions);
        if (focus is null) throw new WriteBackException("台账里的焦点消息快照损坏，无法写回。");

        using var db = SqlCipherNative.OpenReadWrite(pending.ShardPath, _dbKeyHex);
        var current = ReadRow(db, pending.TableName, localId);
        var alreadyOurs = IsOurs(current);

        var active = alreadyOurs ? Ledger.ActiveWriteBack(pending.SessionId, localId) : null;
        var snapshotType = active?.OriginalLocalType ?? pending.OriginalLocalType;
        var snapshotIsText = active?.OriginalContentIsText ?? pending.OriginalContentIsText;
        var snapshotHex = active?.OriginalContentHex ?? pending.OriginalContentHex;
        var snapshotCt = active?.OriginalCompressType ?? pending.OriginalCompressType;

        string title;
        if (alreadyOurs)
        {
            if (snapshotType is null || snapshotHex is null)
                throw new WriteBackRejected("该消息已被引用块改写，但台账里找不到原文快照，拒绝覆盖。");
            title = QuoteMessage.Parse(current.Text)?.Title ?? "";
            if (string.IsNullOrWhiteSpace(title)) title = focus.DisplayText;
        }
        else
        {
            var subType = current.LocalType & 0xFFFFFFFF;
            if (subType != ChatStore.TypeText)
            {
                // 对方正常引用回复（49/appmsg + refermsg，且不是本工具写的）：不改写原行，另外加一条气泡
                if (subType == ChatStore.TypeAppMsg &&
                    current.Text.Contains("<refermsg>", StringComparison.Ordinal))
                    return CommitAddedBubble(pending, db, localId, focus,
                        QuoteMessage.Parse(current.Text)?.Title ?? "");
                throw new WriteBackRejected("只能给对方的文本消息或引用回复加引用块。");
            }

            snapshotType = current.LocalType;
            snapshotIsText = current.IsText;
            snapshotHex = Convert.ToHexString(current.Bytes);
            snapshotCt = current.CompressType;
            title = string.IsNullOrWhiteSpace(focus.DisplayText) ? current.Text : focus.DisplayText;
        }

        // 快照先入台账，再改库：中间崩溃时库仍是原文，对账可安全重试
        Ledger.SaveSnapshot(pending.Id, snapshotType.Value, snapshotIsText, snapshotHex!, snapshotCt);

        var quoteText = pending.QuoteText ?? "";
        var xml = JudgmentXml(quoteText, title, focus, pending.WxId, 0);
        long serverId;
        long judgmentLocalId;
        db.Execute("BEGIN");
        try
        {
            EnsureJudgmentTable(db, JudgmentTable, pending.TableName);
            if (alreadyOurs)
            {
                // 优先按 committed 条目删；台账尚未更新（崩溃恢复）时按 XML 里的 svrid 找回旧行
                if (active?.JudgmentTableName is { } oldTable && active.JudgmentLocalId is { } oldLocalId)
                    db.Execute($"DELETE FROM \"{Esc(oldTable)}\" WHERE local_id = {oldLocalId}");
                else if (QuoteMessage.Parse(current.Text)?.Quote?.RefServerId is { } oldServerId)
                    db.Execute($"DELETE FROM \"{Esc(JudgmentTable)}\" WHERE server_id = {oldServerId}");
            }

            (judgmentLocalId, serverId) = InsertJudgmentRow(db, JudgmentTable, quoteText, pending.WxId);
            xml = JudgmentXml(quoteText, title, focus, pending.WxId, serverId);

            var xmlBytes = Encoding.UTF8.GetBytes(xml);
            // 保持原行的存储类与压缩方式，避免读取端（微信 / 本应用）按错方式解码
            string assignment;
            string compressType;
            if (snapshotIsText)
            {
                assignment = $"'{Esc(xml)}'";
                compressType = "NULL";
            }
            else if (snapshotCt == 4)
            {
                assignment = $"X'{Convert.ToHexString(CompressZstd(xmlBytes))}'";
                compressType = "4";
            }
            else
            {
                assignment = $"X'{Convert.ToHexString(xmlBytes)}'";
                compressType = snapshotCt?.ToString() ?? "NULL";
            }

            var hasCtColumn = db.Query($"PRAGMA table_info(\"{Esc(pending.TableName)}\")")
                .Any(r => string.Equals(r.Text("name"), "WCDB_CT_message_content", StringComparison.OrdinalIgnoreCase));
            db.Execute($"""
                        UPDATE "{Esc(pending.TableName)}"
                        SET local_type = {(57L << 32) | 49},
                            message_content = {assignment}
                            {(hasCtColumn ? $", WCDB_CT_message_content = {compressType}" : "")}
                        WHERE local_id = {localId}
                        """);
            if (db.LastChanges() == 0) throw new WriteBackRejected($"目标行在写回过程中消失（{pending.TableName}#{localId}）");
            db.Execute("COMMIT");
        }
        catch
        {
            try
            {
                db.Execute("ROLLBACK");
            }
            catch
            {
                /* 尽力回滚 */
            }

            throw;
        }

        Ledger.CompleteWriteBack(pending.Id, judgmentLocalId, serverId, JudgmentTable);
        // 同一行只保留一条生效写回（重新写回时旧条目降级为「已被取代」）
        Ledger.SupersedeOtherCommitted(pending.SessionId, localId, pending.Id);
        return Ledger.GetWriteBack(pending.Id)!;
    }

    /// <summary>沿用这条消息已有条目里的原文快照（找不到则原样返回，由 CommitCore 现读现存）。</summary>
    private WriteBackIntent WithCarriedSnapshot(WriteBackIntent intent, string sessionId, long localId)
    {
        var snapshot = Ledger.LatestSnapshotFor(sessionId, localId);
        return snapshot is null
            ? intent
            : intent with
            {
                OriginalLocalType = snapshot.OriginalLocalType,
                OriginalContentIsText = snapshot.OriginalContentIsText,
                OriginalContentHex = snapshot.OriginalContentHex,
                OriginalCompressType = snapshot.OriginalCompressType
            };
    }

    /// <summary>
    ///     目标消息本身是「对方引用我的消息回复」时不改写它：复制发送者与时间，新增一条气泡，
    ///     撤销时按 inserted_local_id 精确删除。气泡带 source 标记，
    ///     读取上下文与实时监听都会排除它，不会当成对方的新消息再判读一遍。
    /// </summary>
    private WriteBackEntry CommitAddedBubble(WriteBackEntry pending, SqlCipherNative db, long targetLocalId,
        ChatMessage focus, string title)
    {
        var meta = db.Query($"""
                             SELECT real_sender_id, create_time, sort_seq FROM "{Esc(pending.TableName)}"
                             WHERE local_id = {targetLocalId}
                             """)[0];
        var realSenderId = meta.Int("real_sender_id");
        var createTime = meta.IntOrNull("create_time") ?? DateTimeOffset.Now.ToUnixTimeSeconds();
        var sortSeq = meta.IntOrNull("sort_seq") ?? createTime * 1000;
        var quoteText = pending.QuoteText ?? "";
        // 同一目标消息只允许一条注解气泡：重新写回时先删掉上一条的气泡与判读行，
        // 否则旧气泡留在微信库里，且旧台账条目已被标记撤销，再也没有入口能删它
        var previous = Ledger.ActiveWriteBack(pending.SessionId, targetLocalId);

        long bubbleLocalId;
        long judgmentLocalId;
        long serverId;
        // 先记账再改库：气泡行号先落台账，崩溃后 Reconcile 才能按它认出「已插入未提交」
        bubbleLocalId = db.Query(
            $"SELECT COALESCE(MAX(local_id),0)+1 AS next FROM \"{Esc(pending.TableName)}\"")[0].Int("next");
        Ledger.SetInsertedRow(pending.Id, bubbleLocalId);
        db.Execute("BEGIN");
        try
        {
            if (previous?.InsertedLocalId is { } oldBubbleLocalId)
                db.Execute($"""
                            DELETE FROM "{Esc(pending.TableName)}"
                            WHERE local_id = {oldBubbleLocalId} AND IFNULL(source,'') = '{ChatStore.AnnotationBubbleSource}'
                            """);
            if (previous?.JudgmentTableName is { } oldJudgmentTable &&
                previous.JudgmentLocalId is { } oldJudgmentLocalId)
                db.Execute($"DELETE FROM \"{Esc(oldJudgmentTable)}\" WHERE local_id = {oldJudgmentLocalId}");
            serverId = RandomServerId();
            var xml = JudgmentXml(quoteText, title, focus, pending.WxId, serverId);
            EnsureJudgmentTable(db, JudgmentTable, pending.TableName);
            (judgmentLocalId, _) = InsertJudgmentRow(db, JudgmentTable, quoteText, pending.WxId);
            db.Execute($"""
                        INSERT INTO "{Esc(pending.TableName)}"
                        (local_id, server_id, local_type, sort_seq, real_sender_id, create_time,
                         status, upload_status, download_status, server_seq, origin_source, source,
                         message_content, compress_content, packed_info_data)
                        VALUES ({bubbleLocalId}, {serverId}, {(57L << 32) | 49}, {sortSeq}, {realSenderId},
                                {createTime}, 4, 0, 0, 0, 5, '{Esc(ChatStore.AnnotationBubbleSource)}',
                                '{Esc(xml)}', NULL, NULL)
                        """);
            db.Execute("COMMIT");
        }
        catch
        {
            try
            {
                db.Execute("ROLLBACK");
            }
            catch
            {
                /* 事务已结束则忽略 */
            }

            throw;
        }

        Ledger.CompleteWriteBack(pending.Id, judgmentLocalId, serverId, JudgmentTable);
        Ledger.SupersedeOtherCommitted(pending.SessionId, targetLocalId, pending.Id);
        return Ledger.GetWriteBack(pending.Id)!;
    }

    private static long RandomServerId()
    {
        return Math.Abs(BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) % 0x0000_FFFF_FFFF_FFFF);
    }

    private static WriteBackIntent Intent(string wxId, string sessionId, ChatMessage focus,
        string quoteText, string? judgmentId)
    {
        return new WriteBackIntent
        {
            JudgmentId = judgmentId,
            WxId = wxId,
            SessionId = sessionId,
            ShardPath = focus.Location.ShardPath,
            TableName = focus.Location.TableName,
            Focus = focus,
            QuoteText = quoteText
        };
    }

    // ── 目标行读取与判定 ─────────────────────────────────────────────

    private static SnapshotRow ReadRow(SqlCipherNative db, string tableName, long localId)
    {
        var rows = db.Query($"""
                             SELECT local_type, typeof(message_content) AS storage, message_content,
                                    WCDB_CT_message_content AS ct
                             FROM "{Esc(tableName)}" WHERE local_id = {localId}
                             """);
        if (rows.Count == 0) throw new WriteBackRejected($"找不到待标注的消息行（{tableName}#{localId}）。");
        var row = rows[0];
        var isText = string.Equals(row.Text("storage"), "text", StringComparison.OrdinalIgnoreCase);
        var bytes = isText
            ? Encoding.UTF8.GetBytes(row.Text("message_content") ?? "")
            : row.Blob("message_content") ?? [];
        var ct = row.IntOrNull("ct");
        var text = isText
            ? Encoding.UTF8.GetString(bytes)
            : (ct == 4 ? ContentDecoder.TryDecompress(bytes) : null) ?? Encoding.UTF8.GetString(bytes);
        return new SnapshotRow(row.Int("local_type"), isText, bytes, ct, text);
    }

    /// <summary>该行是否已被本工具改写为指向伪会话的引用块。</summary>
    private static bool IsOurs(SnapshotRow row)
    {
        return (row.LocalType & 0xFFFFFFFF) == ChatStore.TypeAppMsg &&
               QuoteMessage.Parse(row.Text) is { ReferFromUsr: JudgmentSessionId };
    }

    // ── 微信库 DDL / XML ─────────────────────────────────────────────

    /// <summary>伪会话表：结构与真实 Msg 表一致（首次写回时按目标表结构建）。</summary>
    private static void EnsureJudgmentTable(SqlCipherNative db, string judgmentTable, string sourceTable)
    {
        var exists = db.Query($"SELECT COUNT(*) AS n FROM sqlite_master WHERE name = '{Esc(judgmentTable)}'")[0]
            .Int("n");
        if (exists > 0) return;
        var schema = db.Query($"SELECT sql FROM sqlite_master WHERE name = '{Esc(sourceTable)}'")[0].Text("sql")
                     ?? throw new WriteBackException($"读不到 {sourceTable} 的表结构，无法创建判读表。");
        db.Execute(schema.Replace(sourceTable, judgmentTable));
    }

    /// <summary>插入一条判读文本行（status=4 / origin_source=5：非微信自身写入形态）。</summary>
    private static (long LocalId, long ServerId) InsertJudgmentRow(
        SqlCipherNative db, string judgmentTable, string text, string wxId)
    {
        var serverId = RandomServerId();
        var nextLocalId = db.Query($"SELECT COALESCE(MAX(local_id),0)+1 AS next FROM \"{Esc(judgmentTable)}\"")[0]
            .Int("next");
        db.Execute($"""
                    INSERT INTO "{Esc(judgmentTable)}"
                    (local_id, server_id, local_type, sort_seq, real_sender_id, create_time,
                     status, upload_status, download_status, server_seq, origin_source, source,
                     message_content, compress_content, packed_info_data)
                    VALUES ({nextLocalId}, {serverId}, 1, {DateTimeOffset.Now.ToUnixTimeMilliseconds()}, 1,
                            {DateTimeOffset.Now.ToUnixTimeSeconds()}, 4, 0, 0, 0, 5, '',
                            '{Esc(text)}', NULL, NULL)
                    """);
        _ = wxId;
        return (nextLocalId, serverId);
    }

    /// <summary>写回的引用块：refermsg.fromusr 固定为判读伪会话，其余字段来自目标消息。</summary>
    private static string JudgmentXml(string quoteText, string title, ChatMessage focus, string myWxId, long serverId)
    {
        return QuoteMessage.Build(new QuoteMessage.Parts
        {
            QuoteText = quoteText,
            Title = title,
            MyWxId = myWxId,
            ReferFromUsr = JudgmentSessionId,
            SenderWxId = focus.SenderWxId,
            ReferServerId = serverId
        });
    }

    private static string Esc(string s)
    {
        return s.Replace("'", "''");
    }

    /// <summary>WCDB 的 zstd 压缩列（CT=4）写法：与原行保持同一种压缩方式。</summary>
    private static byte[] CompressZstd(byte[] data)
    {
        using var compressor = new Compressor();
        return compressor.Wrap(data).ToArray();
    }
}
