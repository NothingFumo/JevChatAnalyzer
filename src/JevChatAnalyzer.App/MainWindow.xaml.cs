using Windows.Graphics;
using JevChatAnalyzer.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JevChatAnalyzer.App;

/// <summary>
///     主窗口：关闭按钮 = 最小化到托盘（监听与自动写回继续运行），
///     托盘菜单「退出」或双击托盘恢复。见 App 的 TrayExit/TrayShow。
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 标题栏/任务栏图标：使用随程序分发的多尺寸 ico（打包与免安装一致）
        var icon = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        FitToWorkArea();
        ContentFrame.Navigate(typeof(SessionsPage));
        Nav.SelectedItem = Nav.MenuItems[0];

        // 关闭 = 隐藏到托盘（取消真实关闭；正式退出走托盘菜单）
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
    }

    /// <summary>托盘「退出」请求（App 订阅后执行正式退出）。</summary>
    public event EventHandler<RoutedEventArgs>? TrayExitRequested;

    /// <summary>把窗口限制在工作区内并居中：窗口大于屏幕会把左下角设置按钮裁掉。</summary>
    private void FitToWorkArea()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var width = Math.Min(1280, Math.Max(720, (int)(work.Width * 0.92)));
        var height = Math.Min(860, Math.Max(520, (int)(work.Height * 0.92)));
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2));
    }

    /// <summary>按 Tag 切换页面（页面内的「去设置」等入口用）。</summary>
    public void Navigate(string tag)
    {
        if (string.Equals(tag, "settings", StringComparison.Ordinal))
        {
            // 设置固定在左下角（NavigationView 内置项），不在顶部菜单里重复出现
            Nav.SelectedItem = Nav.SettingsItem;
            return;
        }

        var item = Nav.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), tag, StringComparison.Ordinal));
        if (item is not null) Nav.SelectedItem = item;
    }

    /// <summary>跳到判读历史并按会话名过滤（会话页「记录」/判读结果「查看记录」用）。</summary>
    public void NavigateToHistory(string title)
    {
        Navigate("history");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ContentFrame.Content is HistoryPage page) page.FilterByTitle(title);
        });
    }

    /// <summary>托盘菜单「退出」直接调（App 转发）。</summary>
    public void RequestExit()
    {
        TrayExitRequested?.Invoke(this, new RoutedEventArgs());
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item) return;
        ContentFrame.Navigate(item.Tag?.ToString() switch
        {
            "history" => typeof(HistoryPage),
            _ => typeof(SessionsPage)
        });
    }
}
