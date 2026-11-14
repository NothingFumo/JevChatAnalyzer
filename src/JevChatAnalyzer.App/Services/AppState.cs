using JevChatAnalyzer.Core.Accounts;
using JevChatAnalyzer.Core.Analysis;
using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Judge.Laya;
using JevChatAnalyzer.Core.Keys;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.Voice;
using JevChatAnalyzer.Core.WriteBack;

namespace JevChatAnalyzer.App.Services;

/// <summary>应用级服务编排：设置、密钥库、ChatStore 生命周期、判读管线、转写、历史、写回。</summary>
public sealed class AppState
{
    /// <summary>打开指定账号的微信数据（无参数 = 用保管库当前账号）。返回状态消息。</summary>
    /// <summary>打开/切换/关闭微信数据的唯一入口：串行化，避免两个线程互相释放对方刚开的句柄。</summary>
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private VoiceTranscriber? _transcriber;

    private AppState()
    {
        Settings = SettingsStore.Load();
        // 「监听开着但一个会话都没勾」等于空转：首次启动归一听全部活跃会话。
        if (Settings.MonitorEnabled && !Settings.MonitorAll
            && SessionWhitelist.Parse(Settings.MonitorSessions).Count == 0)
        {
            Settings.MonitorAll = true;
        }
        Pipeline = new AnalysisPipeline(
            new ConnectedChatReader(this),
            new ConnectedVoiceTranscriber(this),
            new LayaConversationJudge(Judge),
            new LedgerAnalysisSink(this),
            SnapshotSettings);
        Flow = new MessageFlow(this, Pipeline);
    }

    public static AppState Instance { get; } = new();

    public SettingsStore SettingsStore { get; } = new();
    public AppSettings Settings { get; } = new();

    public KeyVault KeyVault { get; } = new();

    /// <summary>判读台账：判读历史 + 写回台账 + 待写队列的唯一本地存储。</summary>
    public LedgerStore Ledger { get; } = new();

    /// <summary>写回：连接账号时创建（绑定该账号的密钥），断开时为 null。</summary>
    public AccountWriteBack? WriteBack { get; private set; }

    /// <summary>语音转写器（目录取设置项，空 = 默认文档目录；修改需重启应用）。</summary>
    public VoiceTranscriber Transcriber => _transcriber ??= new VoiceTranscriber(
        string.IsNullOrWhiteSpace(Settings.VoiceModelDir) ? null : Settings.VoiceModelDir);

    /// <summary>判读（本地 laya，唯一来源；引擎懒加载，进程内单实例）。</summary>
    public LayaJudge Judge { get; } = new();

    /// <summary>判读管线：依赖全部走 port，装配在本组合根。</summary>
    public AnalysisPipeline Pipeline { get; }

    /// <summary>最近一次盲起草的候选（判读后端全挂时的兜底产物，供「起草回复」查看）。</summary>
    public IReadOnlyList<DraftSuggestion>? BlindDraftResult { get; set; }

    /// <summary>消息流：轮询判读、落库、对账的唯一排程者（进程内单实例，随账号启停）。</summary>
    public MessageFlow Flow { get; }

    /// <summary>当前已连接的账号（消息库、头像库、密钥、展示名）；未连接为 null。</summary>
    public ConnectedAccount? Account { get; private set; }

    /// <summary>微信数据是否已连接。</summary>
    public bool IsConnected => Account is not null;

    /// <summary>连接状态变化（打开 / 关闭微信数据）。</summary>
    public event Action? ConnectionChanged;

    /// <summary>把状态消息推给界面（后台线程调用安全，订阅方负责切回 UI 线程）。</summary>
    public void ReportStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }

    /// <summary>
    ///     启动时自动连接：有密钥就直接打开微信数据。
    ///     永不抛出——失败只返回原因，界面照常可用（引导卡片提示，不拦截任何操作）。
    /// </summary>
    public string TryAutoConnect()
    {
        if (Account is not null) return "已连接微信数据。";
        var entry = KeyVault.GetCurrent();
        if (entry is null) return "尚未读取微信密钥：请在设置 → 微信密钥里自动读取。";
        try
        {
            return OpenWeChatData();
        }
        catch (Exception ex)
        {
            return $"自动连接微信失败：{ex.Message}";
        }
    }

    public event Action<string>? StatusChanged;

    /// <summary>当前设置里管线需要的字段（每次分析取一次快照）。</summary>
    private AnalysisSettings SnapshotSettings()
    {
        return new AnalysisSettings
        {
            MyWxId = Account?.WxId ?? "",
            ContextTurns = Settings.ContextTurns,
            VoiceEnabled = Settings.VoiceEnabled,
            DraftEnabled = Settings.DraftEnabled,
            DraftEndpoint = Settings.DraftEndpoint,
            DraftModel = Settings.DraftModel
        };
    }

    /// <summary>保存设置。判读 module 不随设置重建（模型目录固定，引擎单实例）。</summary>
    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
    }

    /// <summary>自动读取密钥（rwk.dll）并保存到保管库。返回账号列表。</summary>
    public async Task<IReadOnlyList<WeChatAccountInfo>> ReadKeysAsync()
    {
        var reader = new RwkKeyReader();
        var accounts = await reader.ReadAsync();
        foreach (var account in accounts) KeyVault.Save(account);
        return accounts;
    }

    public string OpenWeChatData(string? wxId = null)
    {
        _connectionGate.Wait();
        try
        {
            return OpenWeChatDataCore(wxId);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private string OpenWeChatDataCore(string? wxId)
    {
        var entry = KeyVault.GetCurrent(wxId);
        if (entry is null) throw new InvalidOperationException("保管库中没有密钥。请先在设置中自动读取或手动输入微信密钥。");

        var locator = new WeChatDataLocator();
        var accountDir = entry.DataPath is not null && Directory.Exists(entry.DataPath)
            ? entry.DataPath
            : locator.ResolveAccountDir(entry.WxId)
              ?? throw new InvalidOperationException($"找不到 {entry.WxId} 的微信数据目录。请确认 xwechat_files 位置。");

        StatusChanged?.Invoke($"正在打开 {entry.WxId} 的消息库 …");
        // 先开新账号：失败时旧账号保持可用，不会被「先释放再打开」弄成半死状态
        var account = ConnectedAccount.Open(accountDir, entry.WxId, entry.DbKeyCipher, entry.NickName);

        // 换引用前先停消息流（Stop 会等在飞 tick 结束），再释放旧句柄
        Flow.Stop();
        var previous = Account;
        Account = account;
        try
        {
            previous?.Dispose();
        }
        catch
        {
            /* 旧句柄释放失败不影响新会话 */
        }

        // 写回模块按账号绑定密钥：页面不再自己拼 dbKey
        WriteBack = new AccountWriteBack(Ledger, account.DbKeyHex);
        KeyVault.SetNickName(entry.WxId, account.DisplayName); // 多账户列表长期可见
        // 消息流负责启动对账、轮询判读与落库
        Flow.Start();
        var sessions = account.Chat.ListSessions(1);
        ConnectionChanged?.Invoke();
        return $"已连接 {account.DisplayName}（{sessions.Count} 个活跃会话采样）" +
               (Settings.MonitorEnabled ? "，实时监听已启动" : "");
    }

    /// <summary>关闭当前微信数据会话（释放库句柄）。</summary>
    public void CloseWeChatData()
    {
        _connectionGate.Wait();
        try
        {
            CloseWeChatDataCore();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private void CloseWeChatDataCore()
    {
        // 断开后不再有机会落库：先停消息流（会等在飞 tick 结束）
        Flow.Stop();
        Account?.Dispose();
        Account = null;
        WriteBack = null;
        ConnectionChanged?.Invoke();
    }

    /// <summary>启动实时监听（打开微信数据后调用；开关关时不做）。</summary>
    public void StartMonitor()
    {
        Flow.Start();
    }

    /// <summary>按最新设置重启监听（白名单/开关变化后热生效）。</summary>
    public void RestartMonitor()
    {
        Flow.Restart();
    }
}
