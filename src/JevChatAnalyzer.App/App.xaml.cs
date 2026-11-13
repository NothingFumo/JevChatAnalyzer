using H.NotifyIcon;
using JevChatAnalyzer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JevChatAnalyzer.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private TaskbarIcon? _tray;

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

    public static Window? MainAppWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例：已有实例在运行时直接退出（托盘里找它）
        _singleInstanceMutex = new Mutex(true, @"Local\JevChatAnalyzer.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Exit();
            return;
        }

        // 判读：本地 laya 单实例。模型已就绪时后台预热，把 1-2 秒的引擎加载挪出首次判读。
        var state = AppState.Instance;
        _ = state.Judge.WarmupAsync();

        MainAppWindow = new MainWindow();
        MainAppWindow.Activate();

        // 启动即自动连接微信数据：后台执行、失败只提示，不阻塞界面也不进入任何模态引导
        _ = Task.Run(() =>
        {
            var message = state.TryAutoConnect();
            state.ReportStatus(message);
        });

        // 托盘：双击图标显示主窗口；关闭窗口 = 最小化到托盘（不退出，监听/自动写回继续）
        _tray = (TaskbarIcon)Resources["TrayIcon"];
        // H.NotifyIcon 的 WinRT 事件默认禁用（Win10 兼容），官方推荐 Command 方式
        var doubleClickCommand = new XamlUICommand();
        doubleClickCommand.ExecuteRequested += (_, _) => ShowMainWindow();
        _tray.DoubleClickCommand = doubleClickCommand;
        // Application XAML 不允许挂事件，托盘菜单在代码里接线
        if (_tray.ContextFlyout is MenuFlyout flyout)
            foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
                switch (item.Name)
                {
                    case "TrayShowItem":
                        item.Click += TrayShow_Click;
                        break;
                    case "TrayExitItem":
                        item.Click += TrayExit_Click;
                        break;
                }

        if (MainAppWindow is MainWindow main)
            main.TrayExitRequested += (_, _) => TrayExit_Click(this, new RoutedEventArgs());
    }

    public void ShowMainWindow()
    {
        MainAppWindow?.AppWindow.Show();
        MainAppWindow?.Activate();
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e)
    {
        ShowMainWindow();
    }

    private async void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        await ShutdownAsync();
    }

    /// <summary>正式退出：停监听、释放判读引擎、释放托盘与单实例互斥量。</summary>
    public async Task ShutdownAsync()
    {
        // 正式退出：先停监听/监视器，再释放判读引擎（约 700MB），最后释放托盘
        try
        {
            AppState.Instance.CloseWeChatData();
        }
        catch
        {
        }

        try
        {
            await AppState.Instance.Judge.DisposeAsync();
        }
        catch
        {
        }

        _tray?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        Exit();
    }
}
