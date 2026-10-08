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
