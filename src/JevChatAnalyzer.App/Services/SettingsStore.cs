using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevChatAnalyzer.App.Services;

/// <summary>应用设置（非敏感部分，明文 JSON；密钥在 KeyVault/DPAPI）。</summary>
public sealed class AppSettings
{
    /// <summary>语音模型目录（空 = 文档\JevChatAnalyzer\models\sensevoice）。</summary>
    public string VoiceModelDir { get; set; } = "";

    /// <summary>语音转写开关。</summary>
    public bool VoiceEnabled { get; set; }

    /// <summary>上下文轮数。</summary>
    public int ContextTurns { get; set; } = 50;

    /// <summary>实时监听：收到对方新消息立即触发分析（需先打开微信数据）。</summary>
    public bool MonitorEnabled { get; set; } = true;

    /// <summary>监听轮询间隔（秒，最小 2）。</summary>
    public int MonitorIntervalSeconds { get; set; } = 5;

    /// <summary>监听全部活跃会话；false 时只监听 <see cref="MonitorSessions" /> 里的会话（空 = 一个都不监听）。</summary>
    public bool MonitorAll { get; set; }

    /// <summary>监听会话白名单：sessionId 列表（逗号/换行分隔）。仅在 <see cref="MonitorAll" /> 为 false 时有意义。</summary>
    public string MonitorSessions { get; set; } = "";

    /// <summary>自动写回：判读对方消息后入队，微信退出后自动落库（本软件的核心动作，默认开）。</summary>
    public bool AutoWriteEnabled { get; set; } = true;

    /// <summary>起草（候选回复）开关与配置。</summary>
    public bool DraftEnabled { get; set; }

    public string DraftEndpoint { get; set; } = "https://api.deepseek.com/chat/completions";

    public string DraftModel { get; set; } = "deepseek-chat";

    /// <summary>起草 API key 所在的环境变量名（密钥不写进 settings.json）。</summary>
    public string DraftApiKeyEnv { get; set; } = "LLM_API_KEY";
}

/// <summary>设置持久化（%LOCALAPPDATA%\JevChatAnalyzer\settings.json）。</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ??
                       new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // 损坏的文件另存备查，用户配置不被静默覆盖成默认值
            TryQuarantine();
        }

        return new AppSettings();
    }

    private void TryQuarantine()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var dir = Path.GetDirectoryName(_path)!;
            File.Move(_path, Path.Combine(dir, $"settings.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"),
                true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // 先写临时文件再原子替换：断电或崩溃不会留下半截 JSON
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, _path, true);
    }
}
