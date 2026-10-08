using JevChatAnalyzer.Core.History;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Keys;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.Voice;
using JevChatAnalyzer.Core.WriteBack;

namespace JevChatAnalyzer.App.Services;

/// <summary>应用级服务编排：设置、密钥库、ChatStore 生命周期、判读管线、转写、历史、写回。</summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    public SettingsStore SettingsStore { get; } = new();
    public AppSettings Settings { get; private set; } = new();

    public KeyVault KeyVault { get; } = new();
    public HistoryStore History { get; } = new();
    public WriteBackService WriteBack { get; } = new();
    public VoiceTranscriber Transcriber { get; } = new();

    public JudgeService Judge { get; private set; } = new();

    /// <summary>当前打开的会话仓库（微信数据直读会话），null = 未打开。</summary>
    public ChatStore? Chat { get; private set; }

    /// <summary>当前账号 wxid。</summary>
    public string? CurrentWxId => Chat?.MyWxId;

    /// <summary>当前账号的明文密钥（判读/解密共用）。</summary>
    public string? CurrentDbKey { get; private set; }

    /// <summary>当前账号数据目录。</summary>
    public string? CurrentAccountDir { get; private set; }

    public event Action<string>? StatusChanged;

    private AppState()
    {
        Settings = SettingsStore.Load();
        RebuildJudgeService();
    }

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        RebuildJudgeService();
    }

    /// <summary>额外后端注册钩子（NLaya 进程内引擎在包可用时挂入）。</summary>
    public Action<JudgeService>? RegisterExtraBackends { get; set; }

    /// <summary>按当前设置重建判读后端（HTTP 总是可用作兜底；进程内引擎就绪时经钩子注册）。</summary>
    public void RebuildJudgeService()
    {
        var old = Judge;
        var service = new JudgeService(() => Settings.JudgeBackend switch
        {
            "local" => "laya 进程内",
            "本地 HTTP 服务r" => "laya 本地服务",
            _ => "云端服务 云端",
        })
        {
            ContextTurns = Settings.ContextTurns,
        };
        service.Register(new HttpBackend(new 线协议BackendOptions
        {
            BaseUri = new Uri(Settings.CloudEndpoint),
            ApiKey = Environment.GetEnvironmentVariable("CLOUD_API_KEY"),
            Model = string.IsNullOrWhiteSpace(Settings.CloudModel) ? null : Settings.CloudModel,
            Sanitize = Settings.CloudSanitize,
        }));
        if (Settings.LayaServerEnabled)
        {
            service.Register(new HttpBackend(new 线协议BackendOptions
            {
                BaseUri = new Uri(Settings.LayaServerUrl),
                Sanitize = false,
            }));
        }
        RegisterExtraBackends?.Invoke(service);
        Judge = service;
        var _ = old; // 旧 service 的释放交给进程退出，避免热切换打断在途请求
    }

    /// <summary>自动读取密钥（rwk.dll）并保存到保管库。返回账号列表。</summary>
    public async Task<IReadOnlyList<WeChatAccountInfo>> ReadKeysAsync()
    {
        var reader = new RwkKeyReader();
        var accounts = await reader.ReadAsync();
        foreach (var account in accounts)
        {
            KeyVault.Save(account, "auto");
        }
        return accounts;
    }

    /// <summary>打开指定账号的微信数据（无参数 = 用保管库当前账号）。返回状态消息。</summary>
    public string OpenWeChatData(string? wxId = null)
    {
        var entry = KeyVault.GetCurrent(wxId);
        if (entry is null)
        {
            throw new InvalidOperationException("保管库中没有密钥。请先在设置中自动读取或手动输入微信密钥。");
        }
        Chat?.Dispose();
        Chat = null;

        var locator = new WeChatDataLocator();
        var accountDir = entry.DataPath is not null && Directory.Exists(entry.DataPath)
            ? entry.DataPath
            : locator.ResolveAccountDir(entry.WxId)
              ?? throw new InvalidOperationException($"找不到 {entry.WxId} 的微信数据目录。请确认 xwechat_files 位置。");

        StatusChanged?.Invoke($"正在打开 {entry.WxId} 的消息库 …");
        Chat = ChatStore.Open(accountDir, entry.WxId, entry.DbKeyCipher);
        CurrentWxIdSet(entry.WxId, entry.DbKeyCipher, accountDir);
        var sessions = Chat.ListSessions(1);
        return $"已打开 {entry.WxId}（{sessions.Count} 个活跃会话采样）";
    }

    private void CurrentWxIdSet(string wxId, string dbKey, string accountDir)
    {
        CurrentDbKey = dbKey;
        CurrentAccountDir = accountDir;
    }

    /// <summary>关闭当前微信数据会话（释放库句柄）。</summary>
    public void CloseWeChatData()
    {
        Chat?.Dispose();
        Chat = null;
        CurrentDbKey = null;
        CurrentAccountDir = null;
    }

    // ── 自动写回：判读入队 → 监视微信退出 → 自动落库 ──

    private System.Threading.Timer? _autoWriteTimer;
    private int _autoWriteBusy;
    private string? _watchedDbKey;

    /// <summary>自动写回完成（成功条目 / 失败原因）。线程池线程上触发。</summary>
    public event Action<IReadOnlyList<(Core.WriteBack.WriteBackEntry? Entry, string? Error)>>? AutoWriteCompleted;

    /// <summary>启动微信退出监视（判读入队后调用；幂等）。落库用开启监视时的密钥快照。</summary>
    public void EnsureAutoWriteWatch()
    {
        if (!Settings.AutoWriteEnabled)
        {
            return;
        }
        _watchedDbKey ??= CurrentDbKey;
        _autoWriteTimer ??= new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _autoWriteBusy, 1) == 1)
            {
                return;
            }
            try
            {
                if (!Settings.AutoWriteEnabled || WriteBack.ListPending().Count == 0)
                {
                    return;
                }
                if (!Core.WriteBack.WriteBackService.IsWeChatClosed())
                {
                    return; // 微信仍在运行，等下一轮
                }
                var key = _watchedDbKey ?? CurrentDbKey;
                if (key is null)
                {
                    return;
                }
                var results = WriteBack.FlushPending(key);
                AutoWriteCompleted?.Invoke(results);
            }
            catch
            {
                // 监视循环内的异常不外抛，下一轮重试
            }
            finally
            {
                Interlocked.Exchange(ref _autoWriteBusy, 0);
            }
        }, null, dueTime: 2000, period: 2000);
    }
}
