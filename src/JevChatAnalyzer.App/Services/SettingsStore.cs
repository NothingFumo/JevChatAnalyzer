using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevChatAnalyzer.App.Services;

/// <summary>应用设置（非敏感部分，明文 JSON；密钥在 KeyVault/DPAPI）。</summary>
public sealed class AppSettings
{
    /// <summary>判读后端：auto（云端优先）/ cloud / local / 本地 HTTP 服务r。</summary>
    public string JudgeBackend { get; set; } = "cloud";

    /// <summary>云端服务 / 本地 HTTP 服务 端点。</summary>
    public string CloudEndpoint { get; set; } = "https://api.example.invalid";

    public string CloudModel { get; set; } = "jev-latest";

    public bool CloudSanitize { get; set; } = true;

    /// <summary>本地 本地 HTTP 服务 地址。</summary>
    public string LayaServerUrl { get; set; } = "http://127.0.0.1:8000";

    /// <summary>NLaya 进程内引擎：模型目录（空 = 默认缓存目录）。</summary>
    public string NlayaModelDir { get; set; } = "";

    public bool NlayaEnabled { get; set; } = true;

    /// <summary>本地 HTTP 服务 HTTP 兜底是否参与后端选择。</summary>
    public bool LayaServerEnabled { get; set; } = false;

    /// <summary>语音转写开关。</summary>
    public bool VoiceEnabled { get; set; } = true;

    /// <summary>上下文轮数。</summary>
    public int ContextTurns { get; set; } = 20;

    /// <summary>实时监听：收到对方新消息立即触发分析（需先打开微信数据）。</summary>
    public bool MonitorEnabled { get; set; } = true;

    /// <summary>监听轮询间隔（秒，最小 2）。</summary>
    public int MonitorIntervalSeconds { get; set; } = 5;

    /// <summary>监听会话白名单：sessionId 列表（逗号/换行分隔；空 = 全部活跃会话）。</summary>
    public string MonitorSessions { get; set; } = "";

    /// <summary>自动写回：判读对方消息后入队，微信退出后自动落库（本软件的核心动作，默认开）。</summary>
    public bool AutoWriteEnabled { get; set; } = true;

    /// <summary>起草（候选回复）开关与配置。</summary>
    public bool DraftEnabled { get; set; } = false;

    public string DraftEndpoint { get; set; } = "https://api.deepseek.com/chat/completions";

    public string DraftModel { get; set; } = "deepseek-chat";
}

/// <summary>设置持久化（%LOCALAPPDATA%\JevChatAnalyzer\settings.json）。</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public SettingsStore(string? appDataDir = null)
    {
        var dir = appDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JevChatAnalyzer");
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
            }
        }
        catch (JsonException)
        {
            // 损坏则回默认
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
