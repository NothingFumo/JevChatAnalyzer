using System.Runtime.InteropServices;
using JevChatAnalyzer.Core.Db;

namespace JevChatAnalyzer.Core.Voice;

/// <summary>语音定位/解码失败。</summary>
public sealed class VoiceException : Exception
{
    public VoiceException(string message) : base(message)
    {
    }
}

/// <summary>一条语音消息的定位与 PCM 解码结果。</summary>
public sealed record VoiceAudio
{
    /// <summary>int16 单声道 PCM，采样率见 <see cref="SampleRate" />。</summary>
    public required short[] Pcm { get; init; }

    public required int SampleRate { get; init; }

    public double DurationSeconds => Pcm.Length / (double)SampleRate;
}

/// <summary>
///     微信语音二进制定位：遍历 media_*.db 的 VoiceInfo 表，
///     匹配链 svr_id → (chat_name_id + create_time ±5s) → local_id。
/// </summary>
public sealed class VoiceLocator
{
    /// <summary>列出账号下全部 media 分片库。</summary>
    public static IReadOnlyList<string> ListMediaDbs(string accountDir)
    {
        var dir = Path.Combine(accountDir, "db_storage", "Media");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "media_*.db").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     在媒体库中定位语音 BLOB。语音消息字段：server_id（svr_id）、senderWxId（发送者 wxid，
    ///     用于 Name2Id → chat_name_id）、createTime（秒）。
    /// </summary>
    public static byte[]? FindVoiceData(string accountDir, string dbKeyHex, long? serverId, string? senderWxId,
        long createTime, long? localId)
    {
        foreach (var mediaDb in ListMediaDbs(accountDir))
        {
            using var db = SqlCipherNative.OpenReadOnly(mediaDb, dbKeyHex);
            var voiceTable = FindVoiceTable(db);
            if (voiceTable is null) continue;
            var columns = VoiceColumns.Detect(db, voiceTable);

            // 策略 A：svr_id 精确命中
            if (serverId is { } sid)
            {
                var blob = TryReadBlob(db, voiceTable, columns,
                    $"SELECT {columns.Data} FROM \"{voiceTable}\" WHERE {columns.SvrId} = {sid}");
                if (blob is not null) return blob;
            }

            // 策略 B：chat_name_id + create_time（±5 秒窗口，同秒多条按顺序取第 N 条）
            var chatNameId = ResolveChatNameId(db, senderWxId);
            if (chatNameId is { } chatId)
            {
                var blob = TryReadBlob(db, voiceTable, columns,
                    $"""
                     SELECT {columns.Data} FROM "{voiceTable}"
                     WHERE {columns.ChatNameId} = {chatId} AND {columns.CreateTime} BETWEEN {createTime - 5} AND {createTime + 5}
                     ORDER BY {columns.CreateTime} LIMIT 1
                     """);
                if (blob is not null) return blob;
            }

            // 策略 C：仅时间窗
            var fallback = TryReadBlob(db, voiceTable, columns,
                $"""
                 SELECT {columns.Data} FROM "{voiceTable}"
                 WHERE {columns.CreateTime} BETWEEN {createTime - 5} AND {createTime + 5}
                 ORDER BY {columns.CreateTime} LIMIT 1
                 """);
            if (fallback is not null) return fallback;
        }

        return null;
    }

    private static string? FindVoiceTable(SqlCipherNative db)
    {
        foreach (var row in db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'VoiceInfo%'"))
            return row.Text("name");
        return null;
    }

    private static long? ResolveChatNameId(SqlCipherNative db, string? wxId)
    {
        if (string.IsNullOrEmpty(wxId)) return null;
        var rows = db.Query($"SELECT rowid FROM Name2Id WHERE user_name = '{wxId.Replace("'", "''")}'");
        return rows.Count > 0 ? rows[0].Int("rowid") : null;
    }

    private static byte[]? TryReadBlob(SqlCipherNative db, string table, VoiceColumns cols, string sql)
    {
        try
        {
            var rows = db.Query(sql);
            if (rows.Count == 0) return null;
            return rows[0].Blob(cols.Data);
        }
        catch (SqliteNativeException)
        {
            return null;
        }
    }

    /// <summary>VoiceInfo 列名自适应（实测存在多种命名）。</summary>
    internal sealed class VoiceColumns
    {
        public string Data { get; private init; } = "voice_data";
        public string SvrId { get; private init; } = "svr_id";
        public string ChatNameId { get; private init; } = "chat_name_id";
        public string CreateTime { get; private init; } = "create_time";

        public static VoiceColumns Detect(SqlCipherNative db, string table)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in db.Query($"PRAGMA table_info(\"{table}\")"))
            {
                var n = row.Text("name");
                if (n is not null) names.Add(n);
            }

            string First(params string[] candidates)
            {
                return candidates.FirstOrDefault(names.Contains) ?? candidates[0];
            }

            return new VoiceColumns
            {
                Data = First("voice_data", "buf", "voicebuf", "data"),
                SvrId = First("svr_id", "msg_svr_id", "server_id", "svrid"),
                ChatNameId = First("chat_name_id", "chatnameid"),
                CreateTime = First("create_time", "createtime")
            };
        }
    }
}

/// <summary>微信同源 silk_decoder.dll 薄壳：Silk v3（可含 \x02#!SILK_V3 头）→ int16 PCM。</summary>
public static class SilkDecoder
{
    [DllImport("silk_decoder", EntryPoint = "silk_decode")]
    private static extern nint silk_decode(byte[] input, int inLen, int sampleRate, out int outLen);

    [DllImport("silk_decoder", EntryPoint = "silk_free")]
    private static extern void silk_free(nint ptr);

    /// <summary>解码 Silk 音频为 PCM。微信语音为 24kHz 单声道。</summary>
    public static VoiceAudio DecodeToPcm(byte[] silkData, int sampleRate = 24000)
    {
        var ptr = silk_decode(silkData, silkData.Length, sampleRate, out var outLen);
        if (ptr == nint.Zero || outLen <= 0) throw new VoiceException("Silk 解码失败：数据损坏或不是有效的微信语音。");
        try
        {
            var bytes = new byte[outLen];
            Marshal.Copy(ptr, bytes, 0, outLen);
            var pcm = new short[bytes.Length / 2];
            for (var i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(bytes, i * 2);
            return new VoiceAudio { Pcm = pcm, SampleRate = sampleRate };
        }
        finally
        {
            silk_free(ptr);
        }
    }

    /// <summary>线性插值重采样（24k → 16k 足够转写用途）。</summary>
    public static short[] Resample(short[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;
        var ratio = (double)fromRate / toRate;
        var outLen = (int)(input.Length / ratio);
        var output = new short[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var src = i * ratio;
            var i0 = (int)src;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = src - i0;
            output[i] = (short)Math.Round(input[i0] * (1 - frac) + input[i1] * frac);
        }

        return output;
    }

    /// <summary>PCM short → float [-1,1]（sherpa-onnx 输入）。</summary>
    public static float[] ToFloat(short[] pcm)
    {
        var result = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++) result[i] = pcm[i] / 32768f;
        return result;
    }
}
