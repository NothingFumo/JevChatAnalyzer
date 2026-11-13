using JevChatAnalyzer.Core.Net;

namespace JevChatAnalyzer.Core.Judge.Laya;

/// <summary>模型就绪状态（设置页显示、判读前置检查共用）。</summary>
public sealed record LayaModelStatus(string Directory, int Present, int Total)
{
    public bool Ready => Present == Total;

    public int Missing => Total - Present;
}

/// <summary>一次下载的结果。</summary>
public sealed record LayaModelResult(string Directory, IReadOnlyList<string> Files);

/// <summary>
///     laya-multilingual checkpoint（HuggingFace convaiinnovations/laya-multilingual）：
///     文件清单、就绪判定、多源下载、目录迁移。
///     迁移只在首次解析默认目录时执行一次；就绪状态每次实查文件（下载完成后立即可见）。
/// </summary>
public sealed class LayaModel
{
    public const string RepoId = "convaiinnovations/laya-multilingual";
    public const string MirrorBase = "https://hf-mirror.com";

    /// <summary>
    ///     checkpoint 相对路径清单（对齐 HF 仓库真实布局，共 6 个文件）。
    ///     NLaya TorchSharp 后端要求 encoder/config.json（编码器构建入口），缺它报 "has no encoder/config.json"。
    /// </summary>
    public static readonly string[] Files =
    [
        "config.json",
        "encoder/config.json",
        "model.safetensors",
        "rl_agent_config.json",
        "tokenizer/tokenizer.json",
        "tokenizer/tokenizer_config.json"
    ];

    private static readonly Lazy<string> DefaultDir = new(ResolveDefault);

    private readonly object _lock = new();
    private Task<LayaModelResult>? _running;

    public LayaModel(string? directory = null)
    {
        Directory = string.IsNullOrWhiteSpace(directory) ? DefaultDir.Value : directory;
    }

    /// <summary>源序列工厂（测试可注入，默认真实源）。</summary>
    internal static Func<IReadOnlyList<string>> BaseUrlsSource { get; set; } = RealBaseUrls;

    /// <summary>checkpoint 目录。</summary>
    public string Directory { get; }

    /// <summary>默认目录：文档\JevChatAnalyzer\models\laya-multilingual（旧版 %LOCALAPPDATA% 目录首次访问时迁移）。</summary>
    public static string DefaultDirectory()
    {
        return DefaultDir.Value;
    }

    private static string ResolveDefault()
    {
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JevChatAnalyzer", "models", "laya-multilingual");
        return ModelDirs.Migrate(ModelDirs.LayaMultilingual(), legacy, Files);
    }

    /// <summary>下载源基址序列（按优先级）：HF_ENDPOINT → huggingface.co → hf-mirror.com。</summary>
    public static IReadOnlyList<string> BaseUrls()
    {
        return BaseUrlsSource();
    }

    private static IReadOnlyList<string> RealBaseUrls()
    {
        var list = new List<string>();
        var endpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint)) list.Add(endpoint.TrimEnd('/'));
        list.Add("https://huggingface.co");
        list.Add(MirrorBase);
        return list;
    }

    /// <summary>当前就绪状态（实查文件，不缓存）。</summary>
    public LayaModelStatus Status()
    {
        var present = Files.Count(f => IsReadyFile(PathOf(f)));
        return new LayaModelStatus(Directory, present, Files.Length);
    }

    /// <summary>下载 checkpoint。已就绪时直接返回；单文件失败自动换下一个源；重复调用复用进行中的任务。</summary>
    public Task<LayaModelResult> EnsureAsync(IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: true }) _running = null;
            return _running ??= RunDownloadAsync(progress, ct);
        }
    }

    private async Task<LayaModelResult> RunDownloadAsync(IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var bases = BaseUrls();
        var downloaded = new List<string>();

        foreach (var rel in Files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = PathOf(rel);
            if (IsReadyFile(dest))
            {
                downloaded.Add(rel);
                continue;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var errors = new List<string>();
            foreach (var baseUrl in bases)
                try
                {
                    var url = $"{baseUrl}/{RepoId}/resolve/main/{rel}";
                    await HttpDownload.DownloadAsync(url, dest, ct, progress,
                        baseUrl.Contains("modelscope", StringComparison.OrdinalIgnoreCase)
                            ? "https://modelscope.cn/"
                            : null);
                    downloaded.Add(rel);
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    errors.Add($"{new Uri(baseUrl).Host}: {e.Message}"); // 401/403/网络中断 → 换下一源
                }

            if (errors.Count == bases.Count)
                throw new InvalidOperationException(
                    $"下载 {rel} 失败（已尝试 {string.Join("、", bases.Select(b => new Uri(b).Host))}）：\n" +
                    string.Join("\n", errors));
        }

        return new LayaModelResult(Directory, downloaded);
    }

    private string PathOf(string relative)
    {
        return Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool IsReadyFile(string path)
    {
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }
}
