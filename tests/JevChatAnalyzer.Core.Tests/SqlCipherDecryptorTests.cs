using System.Security.Cryptography;
using JevChatAnalyzer.Core.Db;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

/// <summary>
/// 解密器字节级往返测试：测试内独立实现加密端（与解密端不共享实现），
/// 构造 SQLCipher 4 页格式的密文（明文页尾按 reserved=80 留零），解密后逐字节比对。
/// 真实微信库的整链路可读性由原生 sqlcipher.dll 主路径覆盖（SqlCipherNative）。
/// </summary>
public class SqlCipherDecryptorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jca-sqlcipher-" + Guid.NewGuid().ToString("N"));

    private const int PageSize = SqlCipherDecryptor.PageSize;

    [Fact]
    public void ParseKeyHex_接受64与96位_拒绝其他()
    {
        var (key64, salt64) = SqlCipherDecryptor.ParseKeyHex(new string('a', 64));
        Assert.Equal(32, key64.Length);
        Assert.Null(salt64);

        var (key96, salt96) = SqlCipherDecryptor.ParseKeyHex(new string('b', 96));
        Assert.Equal(32, key96.Length);
        Assert.Equal(16, salt96!.Length);

        Assert.Throws<SqlCipherException>(() => SqlCipherDecryptor.ParseKeyHex(new string('a', 63)));
        Assert.Throws<SqlCipherException>(() => SqlCipherDecryptor.ParseKeyHex("zz" + new string('a', 62)));
    }

    [Fact]
    public void DecryptToFile_往返保真_页内容与保留区一致()
    {
        var keyHex = NewKeyHex();
        var plain = BuildPlainPages(4);
        var encPath = Path.Combine(_dir, "enc.db");
        TestEncryptor.Encrypt(plain, keyHex, encPath);

        Assert.False(SqlCipherDecryptor.VerifyKey(encPath, new string('f', 64)));
        Assert.True(SqlCipherDecryptor.VerifyKey(encPath, keyHex));

        var outPath = Path.Combine(_dir, "out.db");
        SqlCipherDecryptor.DecryptToFile(encPath, keyHex, outPath);

        Assert.Equal(plain, File.ReadAllBytes(outPath));
    }

    [Fact]
    public void DecryptToFile_合并WAL已提交页_忽略未提交帧()
    {
        var keyHex = NewKeyHex();
        var plain = BuildPlainPages(4);
        var encPath = Path.Combine(_dir, "enc_wal.db");
        TestEncryptor.Encrypt(plain, keyHex, encPath, wal: wal =>
        {
            // 已提交帧：页 2 覆盖为 'W'；未提交帧：页 3 覆盖为 'X'（应被忽略）
            var committed = (byte[])plain.Clone();
            Array.Fill(committed, (byte)'W', PageSize, PageSize - 80);
            var uncommitted = (byte[])plain.Clone();
            Array.Fill(uncommitted, (byte)'X', 2 * PageSize, PageSize - 80);
            wal.AddFrame(2, dbSizeAfterCommit: 4, committed.AsSpan(PageSize).ToArray());
            wal.AddFrame(3, dbSizeAfterCommit: 0, uncommitted.AsSpan(2 * PageSize).ToArray());
        });

        var outPath = Path.Combine(_dir, "out_wal.db");
        SqlCipherDecryptor.DecryptToFile(encPath, keyHex, outPath);

        var output = File.ReadAllBytes(outPath);
        // 页 2 被 WAL 的已提交版本覆盖
        Assert.Equal((byte)'W', output[PageSize]);
        Assert.Equal((byte)'W', output[2 * PageSize - 81]);
        // 页 3 未提交，保持主文件内容
        Assert.Equal(plain[2 * PageSize], output[2 * PageSize]);
        // 页 1 文件头还原为 SQLite magic
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(output, 0, 16));
    }

    [Fact]
    public void DecryptToFile_密钥错误_抛出中文异常()
    {
        var keyHex = NewKeyHex();
        var plain = BuildPlainPages(2);
        var encPath = Path.Combine(_dir, "enc_bad.db");
        TestEncryptor.Encrypt(plain, keyHex, encPath);

        var ex = Assert.Throws<SqlCipherException>(
            () => SqlCipherDecryptor.DecryptToFile(encPath, new string('e', 64), Path.Combine(_dir, "out_bad.db")));
        Assert.Contains("密钥校验失败", ex.Message);
    }

    /// <summary>构造 n 页的明文库：页 1 = magic + 数据 + 80 字节零保留区；其余页同理。</summary>
    private static byte[] BuildPlainPages(int pageCount)
    {
        var data = new byte[pageCount * PageSize];
        "SQLite format 3\0"u8.CopyTo(data);
        var noise = new byte[pageCount * (PageSize - 96)];
        RandomNumberGenerator.Fill(noise);
        noise.CopyTo(data, 16);
        for (var pg = 0; pg < pageCount; pg++)
        {
            Array.Clear(data, pg * PageSize + PageSize - 80, 80); // 保留区清零
        }
        return data;
    }

    private static string NewKeyHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }
}

/// <summary>独立实现的 SQLCipher 4 加密端（仅测试用），页布局：密文 + IV(16) + HMAC-SHA512(64)。</summary>
internal static class TestEncryptor
{
    private const int PageSize = SqlCipherDecryptor.PageSize;

    public static void Encrypt(byte[] plainPages, string keyHex, string encPath, Action<TestWalBuilder>? wal = null)
    {
        var key = Convert.FromHexString(keyHex);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hmacKey = HmacKey(key, salt);

        Directory.CreateDirectory(Path.GetDirectoryName(encPath)!);
        using (var outFs = File.Create(encPath))
        {
            var pageCount = plainPages.Length / PageSize;
            var page = new byte[PageSize];
            for (var pgno = 1; pgno <= pageCount; pgno++)
            {
                var iv = RandomNumberGenerator.GetBytes(16);
                var encStart = pgno == 1 ? 16 : 0;
                var encLen = PageSize - 80 - encStart;
                byte[] cipher;
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;
                    using var enc = aes.CreateEncryptor();
                    cipher = enc.TransformFinalBlock(plainPages, (pgno - 1) * PageSize + encStart, encLen);
                }
                Array.Fill(page, (byte)0);
                if (pgno == 1)
                {
                    salt.CopyTo(page, 0); // 盐占用文件头 16 字节
                }
                cipher.CopyTo(page, encStart);
                iv.CopyTo(page, PageSize - 80);
                using (var hmac = new HMACSHA512(hmacKey))
                {
                    hmac.ComputeHash(page, encStart, encLen + 16).CopyTo(page, PageSize - 64);
                }
                outFs.Write(page);
            }
        }

        if (wal is not null)
        {
            var builder = new TestWalBuilder(encPath + "-wal", key, salt);
            try
            {
                wal(builder);
            }
            finally
            {
                builder.Dispose();
            }
        }
    }

    private static byte[] HmacKey(byte[] key, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(key, salt.Select(b => (byte)(b ^ 0x3A)).ToArray(), 2, HashAlgorithmName.SHA512, 32);

    /// <summary>最小 WAL 写入器：32 字节头 + 帧（24 字节头 + 4096 字节加密页）。</summary>
    public sealed class TestWalBuilder : IDisposable
    {
        private readonly FileStream _fs;
        private readonly byte[] _key;
        private readonly byte[] _salt;
        private readonly byte[] _hmacKey;

        public TestWalBuilder(string path, byte[] key, byte[] salt)
        {
            _fs = File.Create(path);
            _key = key;
            _salt = salt;
            _hmacKey = HmacKey(key, salt);

            var header = new byte[32];
            header[0] = 0x37; header[1] = 0x7f; header[2] = 0x06; header[3] = 0x82;
            _salt.AsSpan(0, 8).CopyTo(header.AsSpan(16)); // salt-1 + salt-2，帧头须与其一致
            _fs.Write(header);
        }

        public void AddFrame(uint pgno, uint dbSizeAfterCommit, byte[] plainPage)
        {
            var header = new byte[24];
            WriteUInt32BE(header.AsSpan(0), pgno);
            WriteUInt32BE(header.AsSpan(4), dbSizeAfterCommit);
            _salt.AsSpan(0, 8).CopyTo(header.AsSpan(8)); // 帧盐须与 WAL 头盐一致

            var iv = RandomNumberGenerator.GetBytes(16);
            byte[] cipher;
            using (var aes = Aes.Create())
            {
                aes.Key = _key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using var enc = aes.CreateEncryptor();
                cipher = enc.TransformFinalBlock(plainPage, 0, PageSize - 80);
            }
            var page = new byte[PageSize];
            cipher.CopyTo(page, 0);
            iv.CopyTo(page, PageSize - 80);
            using (var hmac = new HMACSHA512(_hmacKey))
            {
                hmac.ComputeHash(page, 0, PageSize - 80 + 16).CopyTo(page, PageSize - 64);
            }
            _fs.Write(header);
            _fs.Write(page);
        }

        private static void WriteUInt32BE(Span<byte> span, uint value)
        {
            span[0] = (byte)(value >> 24);
            span[1] = (byte)(value >> 16);
            span[2] = (byte)(value >> 8);
            span[3] = (byte)value;
        }

        public void Dispose() => _fs.Dispose();
    }
}
