using System.Security.Cryptography;
using System.Text;

namespace JevChatAnalyzer.Core.Security;

/// <summary>DPAPI（CurrentUser）加解密，绑定当前 Windows 用户，用于密钥等敏感数据的落盘保护。</summary>
public static class ProtectedStore
{
    // 应用级附加熵：换掉它会导致旧数据无法解密，因此固定为常量。
    private static readonly byte[] Entropy = "JevChatAnalyzer:v1"u8.ToArray();

    public static string ProtectString(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string UnprotectString(string cipherBase64) =>
        Encoding.UTF8.GetString(Unprotect(cipherBase64));

    public static byte[] Unprotect(string cipherBase64) =>
        ProtectedData.Unprotect(Convert.FromBase64String(cipherBase64), Entropy, DataProtectionScope.CurrentUser);
}
