using System.Security.Cryptography;

namespace JevChatAnalyzer.Core.Db;

/// <summary>密钥校验失败或密文损坏。</summary>
public sealed class SqlCipherException : Exception
{
    public SqlCipherException(string message) : base(message) { }
}

/// <summary>
/// 微信 4.x（WCDB / SQLCipher 4）数据库的纯托管页解密。
/// 布局：页大小 4096；页尾保留 80 字节 = IV(16) + HMAC-SHA512(64)；
/// 密钥为 32 字节 raw key（64 hex），盐取文件头前 16 字节；
/// hmac_key = PBKDF2-HMAC-SHA512(key, salt⊕0x3A, 2 次迭代, 32 字节)。
/// 算法与 wechatauto-replica / SQLCipher 4 对齐。
/// </summary>
public static class SqlCipherDecryptor
{
    public const int PageSize = 4096;
    public const int ReserveSize = 80; // IV(16) + HMAC-SHA512(64)
    private const int IvSize = 16;
    private const int HmacSize = 64;
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// 解析密钥 hex：64 位 = 标准 raw key（盐取文件头）；96 位 = SQLCipher「Raw Key with Explicit Salt」
    /// （前 32 字节 key + 后 16 字节盐，明文头模式）。
    /// </summary>
    public static (byte[] Key, byte[]? ExplicitSalt) ParseKeyHex(string keyHex)
    {
        var hex = keyHex.Trim();
        if (hex.Length is not (64 or 96) || hex.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new SqlCipherException($"密钥长度或格式非法（{hex.Length} 字符），需要 64 位 hex。");
        }
        var bytes = Convert.FromHexString(hex);
        return bytes.Length == 32 ? (bytes, null) : (bytes[..32], bytes[32..]);
    }

    /// <summary>校验密钥是否能解开该库（只读第一页并验证 HMAC）。</summary>
    public static bool VerifyKey(string encryptedDbPath, string keyHex)
    {
        try
        {
            using var fs = OpenSharedRead(encryptedDbPath);
            var page1 = new byte[PageSize];
            if (ReadFull(fs, page1) != PageSize)
            {
                return false;
            }
            VerifyPage1Hmac(page1, keyHex);
            return true;
        }
        catch (SqlCipherException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>把加密库解密为明文副本。mergeWal=true 时合并同名 -wal 中已提交的页。</summary>
    public static void DecryptToFile(string encryptedDbPath, string keyHex, string outputPath, bool mergeWal = true)
    {
        var (key, explicitSalt) = ParseKeyHex(keyHex);
        using var src = OpenSharedRead(encryptedDbPath);
        if (src.Length < PageSize)
        {
            throw new SqlCipherException($"文件过小，不是有效的加密库：{encryptedDbPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[PageSize];
        byte[]? hmacKey = null;
        var badPages = 0;
        long pgno = 0;

        int read;
        while ((read = ReadFull(src, buffer)) == PageSize)
        {
            pgno++;
            var page = (byte[])buffer.Clone();
            hmacKey ??= ComputeHmacKey(key, explicitSalt ?? page[..16]);
            if (!VerifyPageHmac(page, hmacKey, pgno, explicitSalt))
            {
                badPages++;
                if (pgno == 1)
                {
                    throw new SqlCipherException(
                        "密钥校验失败（第 1 页 HMAC 不匹配）：dbKey 与该数据库不符，或文件已损坏。" +
                        "请在设置中重新读取或核对密钥。");
                }
            }
            dst.Write(DecryptPage(page, key, pgno, explicitSalt));
        }

        // 尾部不足一页的字节（对齐填充等）原样带过
        if (read > 0)
        {
            dst.Write(buffer, 0, read);
        }

        if (mergeWal && badPages == 0)
        {
            MergeWal(encryptedDbPath + "-wal", key, hmacKey!, explicitSalt, dst);
        }
    }

    /// <summary>合并 WAL 中已提交的页：帧盐须与 WAL 头一致，遇到无效帧即停止；只取到最后一个提交帧为止。</summary>
    private static void MergeWal(string walPath, byte[] key, byte[] hmacKey, byte[]? explicitSalt, FileStream dst)
    {
        if (!File.Exists(walPath))
        {
            return;
        }
        using var wal = OpenSharedRead(walPath);
        var header = new byte[32];
        if (ReadFull(wal, header) != 32)
        {
            return;
        }
        var walSalt = header[16..24]; // salt-1 + salt-2
        var frameHeader = new byte[24];
        var page = new byte[PageSize];
        var frames = new List<(long Pgno, long Offset)>();
        long lastCommitFrame = -1;

        while (ReadFull(wal, frameHeader) == 24)
        {
            if (!frameHeader.AsSpan(8, 8).SequenceEqual(walSalt))
            {
                break; // 帧盐不匹配 = 旧 checkpoint 遗留，其后全部无效
            }
            var pgno = ReadBigEndianUInt32(frameHeader.AsSpan(0, 4));
            var dbSizeAfterCommit = ReadBigEndianUInt32(frameHeader.AsSpan(4, 4));
            var pageOffset = wal.Position;
            if (pgno == 0 || ReadFull(wal, page) != PageSize)
            {
                break;
            }
            if (!VerifyPageHmac(page, hmacKey, pgno, explicitSalt))
            {
                break; // 解不开的帧视为截断/损坏
            }
            frames.Add((pgno, pageOffset));
            if (dbSizeAfterCommit != 0)
            {
                lastCommitFrame = frames.Count;
            }
        }

        foreach (var (pgno, offset) in frames.Take(lastCommitFrame <= 0 ? 0 : (int)lastCommitFrame))
        {
            wal.Seek(offset, SeekOrigin.Begin);
            if (ReadFull(wal, page) != PageSize)
            {
                break;
            }
            var plain = DecryptPage(page, key, pgno, explicitSalt);
            dst.Seek((pgno - 1) * PageSize, SeekOrigin.Begin);
            dst.Write(plain);
        }
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    /// <summary>微信运行时持有库文件的写句柄，读取必须共享读写权限，否则直接 IOException。</summary>
    private static FileStream OpenSharedRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    private static byte[] ComputeHmacKey(byte[] key, byte[] salt)
    {
        var macSalt = new byte[salt.Length];
        for (var i = 0; i < salt.Length; i++)
        {
            macSalt[i] = (byte)(salt[i] ^ 0x3A);
        }
        return Rfc2898DeriveBytes.Pbkdf2(key, macSalt, 2, HashAlgorithmName.SHA512, 32);
    }

    private static void VerifyPage1Hmac(byte[] page1, string keyHex)
    {
        var (key, explicitSalt) = ParseKeyHex(keyHex);
        var hmacKey = ComputeHmacKey(key, explicitSalt ?? page1[..16]);
        if (!VerifyPageHmac(page1, hmacKey, 1, explicitSalt))
        {
            throw new SqlCipherException("密钥校验失败（第 1 页 HMAC 不匹配）。");
        }
    }

    private static bool VerifyPageHmac(byte[] page, byte[] hmacKey, long pgno, byte[]? explicitSalt)
    {
        var start = pgno == 1 && explicitSalt is null ? 16 : 0;
        var hmacDataLen = PageSize - ReserveSize + IvSize - start;
        var stored = page[(PageSize - HmacSize)..];
        using var hmac = new HMACSHA512(hmacKey);
        var expected = hmac.ComputeHash(page, start, hmacDataLen);
        return CryptographicOperations.FixedTimeEquals(expected, stored);
    }

    private static byte[] DecryptPage(byte[] page, byte[] key, long pgno, byte[]? explicitSalt)
    {
        var iv = page[(PageSize - ReserveSize)..(PageSize - ReserveSize + IvSize)];
        var encStart = pgno == 1 && explicitSalt is null ? 16 : 0;
        var encLen = PageSize - ReserveSize - encStart;

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(page, encStart, encLen);

        byte[] result = new byte[PageSize];
        if (pgno == 1)
        {
            if (explicitSalt is null)
            {
                SqliteMagic.CopyTo(result, 0); // 盐位替换回 SQLite 文件头
            }
            else
            {
                page[..16].CopyTo(result, 0);
            }
        }
        plain.CopyTo(result, encStart);
        return result;
    }

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> span) =>
        (uint)(span[0] << 24 | span[1] << 16 | span[2] << 8 | span[3]);
}
