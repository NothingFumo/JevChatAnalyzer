using JevChatAnalyzer.Core.Judge.Laya;
using Xunit;

namespace JevChatAnalyzer.Core.Tests;

public class LayaModelTests
{
    [Fact]
    public void Directory_空串回退默认目录()
    {
        // 未指定目录（空串/空白/null）是常态，必须回退默认而不是拼出相对路径
        Assert.Equal(LayaModel.DefaultDirectory(), new LayaModel("").Directory);
        Assert.Equal(LayaModel.DefaultDirectory(), new LayaModel("   ").Directory);
        Assert.Equal(LayaModel.DefaultDirectory(), new LayaModel().Directory);
        Assert.Equal(@"D:\models", new LayaModel(@"D:\models").Directory);
    }

    [Fact]
    public void Status_空目录_未就绪且不抛异常()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jca-empty-" + Guid.NewGuid().ToString("N"));
        var status = new LayaModel(dir).Status();

        Assert.False(status.Ready);
        Assert.Equal(0, status.Present);
        Assert.Equal(LayaModel.Files.Length, status.Total);
        Assert.Equal(LayaModel.Files.Length, status.Missing);
    }

    [Fact]
    public void Status_文件齐全_就绪_空文件不算就绪()
    {
        var dir = NewDir();
        try
        {
            WriteFiles(dir, "");
            Assert.False(new LayaModel(dir).Status().Ready); // 0 字节文件不算下载完成

            WriteFiles(dir, "x");
            var status = new LayaModel(dir).Status();
            Assert.True(status.Ready);
            Assert.Equal(LayaModel.Files.Length, status.Present);
            Assert.Equal(0, status.Missing);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BaseUrls_含官方与镜像_镜像兜底()
    {
        var urls = LayaModel.BaseUrls();
        Assert.Contains("https://huggingface.co", urls);
        Assert.Contains(LayaModel.MirrorBase, urls);
        // 镜像在官方之后（兜底位）
        Assert.True(urls.ToList().IndexOf("https://huggingface.co") < urls.ToList().IndexOf(LayaModel.MirrorBase));
    }

    [Fact]
    public async Task EnsureAsync_全部源失败时给出聚合错误()
    {
        // 注入全部不可达的源 → 验证异常消息含源主机列表（不依赖真实网络）
        var original = LayaModel.BaseUrlsSource;
        LayaModel.BaseUrlsSource = () => ["http://127.0.0.1:9"];
        var dir = NewDir();
        try
        {
            var model = new LayaModel(dir);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => model.EnsureAsync());
            Assert.Contains("已尝试", ex.Message);
            Assert.Contains("127.0.0.1", ex.Message);
            Assert.False(model.Status().Ready);
        }
        finally
        {
            LayaModel.BaseUrlsSource = original;
            Directory.Delete(dir, true);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jca-laya-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFiles(string dir, string content)
    {
        foreach (var relative in LayaModel.Files)
        {
            var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
