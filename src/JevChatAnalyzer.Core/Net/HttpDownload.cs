using System.Net;
using System.Net.Http.Headers;

namespace JevChatAnalyzer.Core.Net;

/// <summary>下载进度（含速度）。</summary>
public sealed record DownloadProgress(string FileName, long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? BytesReceived * 100.0 / TotalBytes : null;

    /// <summary>当前速度（字节/秒）；单线程流式与分块下载均按秒采样。</summary>
    public double? SpeedBytesPerSecond { get; init; }
}

/// <summary>
///     通用 HTTP 下载器：
///     - 探测：GET Range bytes=0-0 拿总大小 + Accept-Ranges（HttpClient 自动跟随重定向）
///     - 大文件（≥2MB 且支持 Range）4 线程分块并行，按偏移写入同一 .part 文件；
///     小文件/不支持 Range 回退单线程
///     - 每次尝试唯一 .part 临时名；失败清理半成品
///     - 第 1 轮走系统代理，失败第 2 轮直连（系统代理要求认证时所有主机都 401 的兜底）
///     - 浏览器 UA + 可选 Referer（modelscope 防盗链）
///     - 进度带每秒速度采样
/// </summary>
public static class HttpDownload
{
    private const long ChunkedThreshold = 2 * 1024 * 1024;
    private const int ChunkThreads = 4;

    public static async Task DownloadAsync(string url, string destPath, CancellationToken ct,
        IProgress<DownloadProgress>? progress = null, string? referer = null)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
            try
            {
                await CoreAsync(url, destPath, ct, progress, referer, attempt == 1);
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

        throw last!;
    }

    private static async Task CoreAsync(string url, string destPath, CancellationToken ct,
        IProgress<DownloadProgress>? progress, string? referer, bool direct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        var tmp = $"{destPath}.{Guid.NewGuid():N}.part";
        try
        {
            var probe = await ProbeAsync(url, ct, referer, direct);
            var displayName = Path.GetFileName(destPath);
            if (probe.TotalSize >= ChunkedThreshold && probe.AcceptRanges)
                await DownloadChunkedAsync(url, tmp, displayName, probe, ct, progress, referer, direct);
            else
                await DownloadSingleAsync(probe.FinalUrl, tmp, displayName, ct, progress, referer, direct);
            await MoveWithRetry(tmp, destPath);
        }
        catch
        {
            TryDelete(tmp); // 失败清理半成品
            throw;
        }
    }

    // ── 探测：Range 0-0 拿总大小与分块支持 ──
    private static async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct,
        string? referer, bool direct)
    {
        using var handler = new HttpClientHandler { UseProxy = !direct, AllowAutoRedirect = true };
        using var http = BuildClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        ApplyHeaders(request, referer);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = 0;
        var acceptRanges = false;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            total = range?.Length ?? response.Content.Headers.ContentLength ?? 0;
            acceptRanges = true;
        }
        else
        {
            total = response.Content.Headers.ContentLength ?? 0;
        }

        return new ProbeResult(total, acceptRanges, url);
    }

    // ── 4 线程分块并行 ──
    private static async Task DownloadChunkedAsync(string url, string tmp, string displayName, ProbeResult probe,
        CancellationToken ct, IProgress<DownloadProgress>? progress, string? referer, bool direct)
    {
        var total = probe.TotalSize;
        await using var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Write, 1, true);
        file.SetLength(total);

        var chunkSize = total / ChunkThreads + (total % ChunkThreads == 0 ? 0 : 1);
        var downloadedTotal = 0L;
        var lastReported = 0L;
        var lastTime = DateTimeOffset.Now;
        double speed = 0;
        var gate = new object();

        async Task DownloadChunk(int index)
        {
            var start = index * chunkSize;
            var end = index == ChunkThreads - 1 ? total - 1 : (index + 1) * chunkSize - 1;
            using var handler = new HttpClientHandler { UseProxy = !direct };
            using var http = BuildClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            ApplyHeaders(request, referer);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new InvalidOperationException($"分块下载失败：HTTP {(int)response.StatusCode}");
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[1 << 17];
            var offset = start;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                RandomAccess.Write(file.SafeFileHandle, buffer.AsSpan(0, read), offset);
                offset += read;
                lock (gate)
                {
                    downloadedTotal += read;
                }
            }
        }

        // 报告器：分块完成后立即取消（避免迟到的进度与下一个文件交错造成显示横跳）
        using var reporterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reporter = Task.Run(async () =>
        {
            while (!reporterCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, reporterCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                long current;
                lock (gate)
                {
                    current = downloadedTotal;
                }

                var now = DateTimeOffset.Now;
                var secs = (now - lastTime).TotalSeconds;
                if (secs > 0.3)
                {
                    speed = (current - lastReported) / secs;
                    lastReported = current;
                    lastTime = now;
                }

                progress?.Report(new DownloadProgress(displayName, current, total)
                {
                    SpeedBytesPerSecond = speed
                });
            }
        }, CancellationToken.None);

        try
        {
            var chunks = Enumerable.Range(0, ChunkThreads).Select(DownloadChunk).ToArray();
            await Task.WhenAll(chunks);
        }
        finally
        {
            reporterCts.Cancel();
            try
            {
                await reporter;
            }
            catch (Exception)
            {
            }

            // 每个文件只发一次 100% 终态
            progress?.Report(new DownloadProgress(displayName, total, total)
            {
                SpeedBytesPerSecond = 0
            });
        }
    }

    // ── 单线程流式（小文件 / 不支持 Range 回退）──
    private static async Task DownloadSingleAsync(string url, string tmp, string displayName, CancellationToken ct,
        IProgress<DownloadProgress>? progress, string? referer, bool direct)
    {
        using var handler = new HttpClientHandler { UseProxy = !direct };
        using var http = BuildClient(handler);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(tmp);
        var buffer = new byte[1 << 17];
        long received = 0;
        var lastTime = DateTimeOffset.Now;
        double speed = 0;
        var lastReported = 0L;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            var now = DateTimeOffset.Now;
            var secs = (now - lastTime).TotalSeconds;
            if (secs > 0.5)
            {
                speed = (received - lastReported) / secs;
                lastReported = received;
                lastTime = now;
            }

            progress?.Report(new DownloadProgress(displayName, received, total)
            {
                SpeedBytesPerSecond = speed
            });
        }

        if (total is { } expected && received != expected)
            // 镜像返回 HTML 错误页/短包时长度对不上：宁可失败重试，也不能把坏文件当模型就绪
            throw new IOException($"下载不完整：{displayName} 收到 {received}/{expected} 字节。");
        progress?.Report(new DownloadProgress(displayName, received, total)
        {
            SpeedBytesPerSecond = 0
        });
    }

    private static HttpClient BuildClient(HttpClientHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return http;
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? referer)
    {
        if (!string.IsNullOrEmpty(referer)) request.Headers.Referrer = new Uri(referer);
    }

    private static async Task MoveWithRetry(string tmp, string destPath)
    {
        for (var i = 0;; i++)
            try
            {
                File.Move(tmp, destPath, true);
                return;
            }
            catch (IOException) when (i < 3)
            {
                await Task.Delay(200 * (i + 1));
            }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed record ProbeResult(long TotalSize, bool AcceptRanges, string FinalUrl);
}
