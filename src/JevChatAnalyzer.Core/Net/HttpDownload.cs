using JevChatAnalyzer.Core.Voice;

namespace JevChatAnalyzer.Core.Net;

/// <summary>下载进度。</summary>
public sealed record FileDownloadProgress(string FileName, long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? BytesReceived * 100.0 / TotalBytes : null;
}

/// <summary>
/// 通用 HTTP 下载器：
/// - 每次尝试使用唯一 .part 临时名（根治杀软/索引器/上轮残留导致的「文件被另一进程使用」）
/// - 第 1 轮走系统代理，失败后第 2 轮直连（系统代理要求认证时所有主机都会 401，直连可解）
/// - 浏览器 User-Agent；File.Move 带短暂重试（瞬时占用）
/// </summary>
public static class HttpDownload
{
    public static async Task DownloadAsync(string url, string destPath, CancellationToken ct,
        IProgress<DownloadProgress>? progress = null)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await CoreAsync(url, destPath, ct, progress, direct: attempt == 1);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (attempt == 0)
            {
                last = e; // 换直连再试一次
            }
        }
        throw last!;
    }

    private static async Task CoreAsync(string url, string destPath, CancellationToken ct,
        IProgress<DownloadProgress>? progress, bool direct)
    {
        using var handler = new HttpClientHandler { UseProxy = !direct };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 JevChatAnalyzer/1.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var src = await response.Content.ReadAsStreamAsync(ct);

        // 唯一临时名：任何进程（含上一次失败尝试/杀软）都不可能持有同一个文件
        var tmp = $"{destPath}.{Guid.NewGuid():N}.part";
        try
        {
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
            await MoveWithRetry(tmp, destPath);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static async Task MoveWithRetry(string tmp, string destPath)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                File.Move(tmp, destPath, overwrite: true);
                return;
            }
            catch (IOException) when (i < 3)
            {
                await Task.Delay(200 * (i + 1));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
