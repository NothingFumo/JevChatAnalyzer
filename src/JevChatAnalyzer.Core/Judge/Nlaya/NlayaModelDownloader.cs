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
/// 主源 huggingface.co，可配 HF_ENDPOINT 镜像；文件落盘前原子改名，断点不续传（重下覆盖）。
/// </summary>
public static class NlayaModelDownloader
{
    public const string RepoId = "convaiinnovations/laya-multilingual";

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

    private static string BaseUrl()
    {
        var endpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT");
        return string.IsNullOrWhiteSpace(endpoint) ? "https://huggingface.co" : endpoint.TrimEnd('/');
    }

    /// <summary>模型是否已就绪（目录内关键文件齐全且非空）。</summary>
    public static bool IsModelReady(string? dir = null)
    {
        var root = dir ?? DefaultDirectory();
        return Files.All(f => IsReadyFile(Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static bool IsReadyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>下载 checkpoint。已就绪时直接返回。</summary>
    public static async Task<NlayaModelResult> DownloadAsync(
        string? targetDir = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var root = targetDir ?? DefaultDirectory();
        Directory.CreateDirectory(root);
        var baseUrl = BaseUrl();
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
            var url = $"{baseUrl}/{RepoId}/resolve/main/{rel}";
            await DownloadFileAsync(url, dest, ct, progress);
            downloaded.Add(rel);
        }
        return new NlayaModelResult(root, downloaded);
    }

    /// <summary>删除模型目录（设置页的移除按钮）。</summary>
    public static void DeleteModel(string? dir = null)
    {
        var root = dir ?? DefaultDirectory();
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
