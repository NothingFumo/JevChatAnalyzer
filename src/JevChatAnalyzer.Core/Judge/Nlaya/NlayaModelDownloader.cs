using System.Security.Cryptography;

using JevChatAnalyzer.Core.Net;
using JevChatAnalyzer.Core.Voice;

namespace JevChatAnalyzer.Core.Judge.NlayaJudge;

/// <summary>laya checkpoint 文件组下载结果。</summary>
public sealed record NlayaModelResult(string Directory, IReadOnlyList<string> Files)
{
    /// <summary>关键文件齐全即认为可用（TorchSharp 直载 safetensors + 配置 + 分词器）。</summary>
    public bool IsComplete => Files.Count >= 5;
}

/// <summary>
/// laya-multilingual checkpoint 下载器：从 HuggingFace 拉取 PyTorch 权重与配置
/// （NLaya TorchSharp 后端直载 safetensors，无需等待官方 ONNX 导出发布）。
/// 源顺序：HF_ENDPOINT（如设置）→ huggingface.co → hf-mirror.com（镜像，匿名 401/403 时的兜底）；
/// 文件落盘前原子改名；目录为空串/空白时回退默认目录（设置项未填写是常态）。
/// </summary>
public static class NlayaModelDownloader
{
    public const string RepoId = "convaiinnovations/laya-multilingual";
    public const string MirrorBase = "https://hf-mirror.com";

    /// <summary>
    /// checkpoint 相对路径清单（对齐 HF 仓库真实布局，共 6 个文件）。
    /// 注意 NLaya TorchSharp 后端要求 encoder/config.json（编码器构建入口），
    /// 缺它报 "has no encoder/config.json"。
    /// </summary>
    public static readonly string[] Files =
    [
        "config.json",
        "encoder/config.json",
        "model.safetensors",
        "rl_agent_config.json",
        "tokenizer/tokenizer.json",
        "tokenizer/tokenizer_config.json",
    ];

    public static string DefaultDirectory()
    {
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JevChatAnalyzer", "models", "laya-multilingual");
        return ModelDirs.Migrate(ModelDirs.LayaMultilingual(), legacy, Files);
    }

    /// <summary>进行中下载的去重表（同一目录只允许一个下载任务）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.Task> RunningDownloads = new();

    /// <summary>目录解析：null/空串/空白 → 默认目录（设置项允许留空）。</summary>
    public static string ResolveDirectory(string? dir) =>
        string.IsNullOrWhiteSpace(dir) ? DefaultDirectory() : dir;

    /// <summary>源序列工厂（测试可注入，默认真实源）。</summary>
    internal static Func<IReadOnlyList<string>> BaseUrlsSource { get; set; } = RealBaseUrls;

    /// <summary>下载源基址序列（按优先级）。</summary>
    public static IReadOnlyList<string> BaseUrls() => BaseUrlsSource();

    private static IReadOnlyList<string> RealBaseUrls()
    {
        var list = new List<string>();
        var endpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            list.Add(endpoint.TrimEnd('/'));
        }
        list.Add("https://huggingface.co");
        list.Add(MirrorBase);
        return list;
    }

    /// <summary>文件就绪度（已存在/总数），供设置页显示。</summary>
    public static (int Present, int Total) FileStatus(string? dir = null)
    {
        var root = ResolveDirectory(dir);
        var present = Files.Count(f => IsReadyFile(Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar))));
        return (present, Files.Length);
    }

    /// <summary>模型是否已就绪（目录内关键文件齐全且非空）。</summary>
    public static bool IsModelReady(string? dir = null)
    {
        var root = ResolveDirectory(dir);
        return Files.All(f => IsReadyFile(Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static bool IsReadyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>下载 checkpoint。已就绪时直接返回。单文件失败自动换下一个源。重复调用复用进行中的任务。</summary>
    public static Task<NlayaModelResult> DownloadAsync(
        string? targetDir = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var root = ResolveDirectory(targetDir);
        return (Task<NlayaModelResult>)RunningDownloads.GetOrAdd(root, _ => RunDownloadAsync(root, progress, ct));
    }

    private static async Task<NlayaModelResult> RunDownloadAsync(string root,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var bases = BaseUrls();
        var downloaded = new List<string>();

        foreach (var rel in Files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (IsReadyFile(dest))
            {
                downloaded.Add(rel);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var errors = new List<string>();
            foreach (var baseUrl in bases)
            {
                try
                {
                    var url = $"{baseUrl}/{RepoId}/resolve/main/{rel}";
                    await HttpDownload.DownloadAsync(url, dest, ct, progress,
                        referer: baseUrl.Contains("modelscope", StringComparison.OrdinalIgnoreCase)
                            ? "https://modelscope.cn/" : null);
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
            }
            if (downloaded.Count == 0 || errors.Count == bases.Count)
            {
                throw new InvalidOperationException(
                    $"下载 {rel} 失败（已尝试 {string.Join("、", bases.Select(b => new Uri(b).Host))}）：\n" +
                    string.Join("\n", errors));
            }
        }
        return new NlayaModelResult(root, downloaded);
    }

    /// <summary>删除模型目录（设置页的移除按钮）。</summary>
    public static void DeleteModel(string? dir = null)
    {
        var root = ResolveDirectory(dir);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
