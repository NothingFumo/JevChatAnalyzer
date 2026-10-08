using Microsoft.UI.Xaml;
using JevChatAnalyzer.App.Services;
using JevChatAnalyzer.Core.Judge.NlayaJudge;

namespace JevChatAnalyzer.App;

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // 全局异常日志：任何未处理异常写入 crash.log（不再无声闪退）
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JevChatAnalyzer");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}\n{e.Exception}\n\n");
            }
            catch
            {
                // 日志失败不能再次抛出
            }
            e.Handled = true; // 记录后继续运行，避免闪退
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 进程内 laya 引擎：模型就绪时注册为判读后端（HTTP 后端永远保留为兜底）
        var state = AppState.Instance;
        var nlaya = new NlayaBackend(string.IsNullOrWhiteSpace(state.Settings.NlayaModelDir)
            ? null
            : state.Settings.NlayaModelDir);
        state.RegisterExtraBackends = service =>
        {
            if (state.Settings.NlayaEnabled && nlaya.IsModelDownloaded)
            {
                service.Register(nlaya);
            }
        };
        state.RebuildJudgeService();

        MainAppWindow = new MainWindow();
        MainAppWindow.Activate();
    }
}
