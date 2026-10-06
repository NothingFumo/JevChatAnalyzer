namespace JevChatAnalyzer.Core.Keys;

/// <summary>rwk.dll 返回的单个微信账号密钥信息。</summary>
public sealed record WeChatAccountInfo
{
    /// <summary>微信 wxid（如 wxid_xxxx）。</summary>
    public required string WxId { get; init; }

    /// <summary>账号昵称（可能为空）。</summary>
    public string? NickName { get; init; }

    /// <summary>自定义微信号（可能为空）。</summary>
    public string? Alias { get; init; }

    /// <summary>绑定手机号（可能为空，仅用于展示）。</summary>
    public string? Phone { get; init; }

    /// <summary>数据库密钥：64 位小写 hex，即 32 字节 SQLCipher 原始密钥。</summary>
    public required string DbKey { get; init; }

    /// <summary>图片解密 XOR 密钥（原始字符串，交由图片模块解析）。</summary>
    public string? ImageXorKey { get; init; }

    /// <summary>图片解密 AES 密钥（原始字符串，交由图片模块解析）。</summary>
    public string? ImageAesKey { get; init; }

    /// <summary>微信数据目录（xwechat_files 下的账号目录）。</summary>
    public string? DataPath { get; init; }

    /// <summary>检测到的微信版本号。</summary>
    public string? WeixinVersion { get; init; }

    public bool IsValidDbKey =>
        DbKey.Length == 64 && DbKey.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
