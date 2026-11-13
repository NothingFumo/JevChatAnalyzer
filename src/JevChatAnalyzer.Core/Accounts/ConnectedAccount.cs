using JevChatAnalyzer.Core.Avatars;
using JevChatAnalyzer.Core.Messages;

namespace JevChatAnalyzer.Core.Accounts;

/// <summary>
///     当前账号的已连接状态：消息库、头像库、密钥、数据目录与展示名。
///     构造即完成打开；任一步失败都会释放已开句柄，不留半开状态。
/// </summary>
public sealed class ConnectedAccount : IDisposable
{
    private bool _disposed;

    private ConnectedAccount(string wxId, string dbKeyHex, string accountDir,
        ChatStore chat, AvatarStore avatars, string displayName)
    {
        WxId = wxId;
        DbKeyHex = dbKeyHex;
        AccountDir = accountDir;
        Chat = chat;
        Avatars = avatars;
        DisplayName = displayName;
    }

    public string WxId { get; }

    /// <summary>账号的库密钥：写回与语音定位共用，页面不再各自持有。</summary>
    public string DbKeyHex { get; }

    public string AccountDir { get; }

    public ChatStore Chat { get; }

    public AvatarStore Avatars { get; }

    /// <summary>界面上展示的账号名（不展示 wxid）。</summary>
    public string DisplayName { get; }

    public string? AvatarPath => Avatars.GetAvatarPath(WxId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Chat.Dispose();
        Avatars.Dispose();
    }

    /// <summary>
    ///     打开一个账号。展示名取微信联系人里的备注/昵称；解析不到（等于 wxid）时用 fallbackName，
    ///     再退回「当前账号」。
    /// </summary>
    public static ConnectedAccount Open(string accountDir, string wxId, string dbKeyHex, string? fallbackName = null)
    {
        var chat = ChatStore.Open(accountDir, wxId, dbKeyHex);
        AvatarStore avatars;
        try
        {
            avatars = new AvatarStore(accountDir, dbKeyHex);
        }
        catch
        {
            chat.Dispose();
            throw;
        }

        var resolved = chat.ResolveName(wxId);
        var displayName = string.IsNullOrWhiteSpace(resolved) ||
                          string.Equals(resolved, wxId, StringComparison.Ordinal)
            ? string.IsNullOrWhiteSpace(fallbackName) ? "当前账号" : fallbackName
            : resolved;
        return new ConnectedAccount(wxId, dbKeyHex, accountDir, chat, avatars, displayName);
    }
}
