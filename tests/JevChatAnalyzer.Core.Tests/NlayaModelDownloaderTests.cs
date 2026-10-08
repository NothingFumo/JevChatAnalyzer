using JevChatAnalyzer.Core.Judge.NlayaJudge;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class NlayaModelDownloaderTests
{
    [Fact]
    public void ResolveDirectory_空串回退默认目录()
    {
        // 设置项未填写（空串）是常态，必须回退默认而不是 Path.Combine("") 抛异常
        Assert.Equal(NlayaModelDownloader.DefaultDirectory(), NlayaModelDownloader.ResolveDirectory(""));
        Assert.Equal(NlayaModelDownloader.DefaultDirectory(), NlayaModelDownloader.ResolveDirectory("   "));
        Assert.Equal(NlayaModelDownloader.DefaultDirectory(), NlayaModelDownloader.ResolveDirectory(null));
        Assert.Equal(@"D:\models", NlayaModelDownloader.ResolveDirectory(@"D:\models"));
    }

    [Fact]
    public void IsModelReady_空目录不抛异常()
    {
        Assert.False(NlayaModelDownloader.IsModelReady(""));
        Assert.False(NlayaModelDownloader.IsModelReady(null));
    }

    [Fact]
    public void BaseUrls_含官方与镜像_镜像兜底()
    {
        var urls = NlayaModelDownloader.BaseUrls();
        Assert.Contains("https://huggingface.co", urls);
        Assert.Contains(NlayaModelDownloader.MirrorBase, urls);
        // 镜像在官方之后（兜底位）
        Assert.True(urls.ToList().IndexOf("https://huggingface.co") < urls.ToList().IndexOf(NlayaModelDownloader.MirrorBase));
    }

    [Fact]
    public async Task DownloadAsync_全部源失败时给出聚合错误()
    {
        // 注入全部不可达的源 → 验证异常消息含源主机列表（不依赖真实网络）
        var original = NlayaModelDownloader.BaseUrlsSource;
        NlayaModelDownloader.BaseUrlsSource = () => ["http://127.0.0.1:9"];
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "jca-dl-" + Guid.NewGuid().ToString("N"));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NlayaModelDownloader.DownloadAsync(dir));
            Assert.Contains("已尝试", ex.Message);
            Assert.Contains("127.0.0.1", ex.Message);
            Assert.False(NlayaModelDownloader.IsModelReady(dir));
        }
        finally
        {
            NlayaModelDownloader.BaseUrlsSource = original;
        }
    }
}
