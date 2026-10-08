using System.Security.Cryptography;

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

    /// <summary>checkpoint 相对路径清单（共 5 个关键文件）。</summary>
    public static readonly string[] Files =
    [
        "config.json",
        "model.safetensors",
        "rl_agent_config.json",
        "tokenizer/tokenizer.json",
        "tokenizer/tokenizer_config.json",
    ];

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JevChatAnalyzer", "models", "laya-multilingual");

    /// <summary>目录解析：null/空串/空白 → 默认目录（设置项允许留空）。</summary>
    public static string ResolveDirectory(string? dir) =>
        string.IsNullOrWhiteSpace(dir) ? DefaultDirectory() : dir;

    /// <summary>下载源基址序列（按优先级）。</summary>
    public static IReadOnlyList<string> BaseUrls()
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

    /// <summary>模型是否已就绪（目录内关键文件齐全且非空）。</summary>
    public static bool IsModelReady(string? dir = null)
    {
        var root = ResolveDirectory(dir);
        return Files.All(f => IsReadyFile(Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static bool IsReadyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>下载 checkpoint。已就绪时直接返回。单文件失败自动换下一个源。</summary>
    public static async Task<NlayaModelResult> DownloadAsync(
        string? targetDir = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var root = ResolveDirectory(targetDir);
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

            Exception? lastError = null;
            foreach (var baseUrl in bases)
            {
                try
                {
                    var url = $"{baseUrl}/{RepoId}/resolve/main/{rel}";
                    await DownloadFileAsync(url, dest, ct, progress);
                    downloaded.Add(rel);
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    lastError = e; // 401/403/网络中断 → 换下一个源
                }
            }
            if (lastError is not null)
            {
                throw new InvalidOperationException(
                    $"下载 {rel} 失败（已尝试 {string.Join("、", bases.Select(b => new Uri(b).Host))}）：{lastError.Message}",
                    lastError);
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

    private static async Task DownloadFileAsync(string url, string destPath, CancellationToken ct, IProgress<DownloadProgress>? progress)
    {
        var tmp = destPath + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(tmp);
        var buffer = new byte[1 << 18];
        long received = 0;
        var lastReport = DateTimeOffset.Now;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            if (progress is not null && (DateTimeOffset.Now - lastReport).TotalMilliseconds > 300)
            {
                progress.Report(new DownloadProgress(Path.GetFileName(destPath), received, total));
                lastReport = DateTimeOffset.Now;
            }
        }
        progress?.Report(new DownloadProgress(Path.GetFileName(destPath), received, total));
        File.Move(tmp, destPath, overwrite: true);
    }
}
