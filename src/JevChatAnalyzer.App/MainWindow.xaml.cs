using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JevChatAnalyzer.App.Views;

namespace JevChatAnalyzer.App;

/// <summary>
/// 主窗口：关闭按钮 = 最小化到托盘（监听与自动写回继续运行），
/// 托盘菜单「退出」或双击托盘恢复。见 App 的 TrayExit/TrayShow。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>托盘「退出」请求（App 订阅后执行正式退出）。</summary>
    public event EventHandler<RoutedEventArgs>? TrayExitRequested;

    public MainWindow()
    {
        InitializeComponent();
        ContentFrame.Navigate(typeof(AnalyzePage));
        Nav.SelectedItem = Nav.MenuItems[0];

        // 关闭 = 隐藏到托盘（取消真实关闭；正式退出走托盘菜单）
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
    }

    /// <summary>托盘菜单「退出」直接调（App 转发）。</summary>
    public void RequestExit() => TrayExitRequested?.Invoke(this, new RoutedEventArgs());

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }
        ContentFrame.Navigate(item.Tag?.ToString() switch
        {
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(AnalyzePage),
        });
    }
}
