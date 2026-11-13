using System.Text.RegularExpressions;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using JevChatAnalyzer.App.Services;
using JevChatAnalyzer.Core.Keys;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WinRT.Interop;

namespace JevChatAnalyzer.App.Views;

/// <summary>设置页：微信密钥 / 判读后端 / 本地引擎模型 / 语音转写 / 隐私与写回 / 关于。</summary>
public sealed partial class SettingsPage : Page
{
    private CancellationTokenSource? _layaDownloadCts;
    private CancellationTokenSource? _voiceDownloadCts;

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
        AppState.Instance.ConnectionChanged += () => DispatcherQueue.TryEnqueue(RefreshAccounts);
    }

    /// <summary>页面已开启导航缓存（订阅只注册一次），每次进入时刷新一次可见状态。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = AppState.Instance.Settings;
        VoiceSwitch.IsOn = s.VoiceEnabled;
        VoiceModelDirBox.Text = s.VoiceModelDir;
        WriteBackSwitch.IsOn = s.AutoWriteEnabled;
        ElevateButton.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;

        RefreshAccounts();
        ShowModelStatus();
        var transcriber = AppState.Instance.Transcriber;
        VoiceModelStatus.Text = transcriber.IsModelReady
            ? $"模型已就绪：{transcriber.ModelFiles.ModelPath}（{FormatSize(new FileInfo(transcriber.ModelFiles.ModelPath).Length)}）"
            : "模型未下载。";
    }

    // ── 多账号：密钥持久化 / 切换 / 数据目录 ─────────────────────────

    private void RefreshAccounts()
    {
        var state = AppState.Instance;
        var current = state.Account?.WxId;
        var rows = new List<AccountRow>();
        foreach (var account in state.KeyVault.ListAccounts())
        {
            var isCurrent = string.Equals(account.WxId, current, StringComparison.Ordinal);
            var name = !string.IsNullOrWhiteSpace(account.NickName)
                ? account.NickName!
                : isCurrent && !string.IsNullOrWhiteSpace(state.Account?.DisplayName)
                    ? state.Account!.DisplayName
                    : "未命名账号";
            var path = string.IsNullOrWhiteSpace(account.DataPath) ? "数据目录：自动定位" : account.DataPath!;
            rows.Add(new AccountRow(account.WxId, name, path, isCurrent,
                isCurrent ? state.Account?.AvatarPath : null));
        }

        AccountList.ItemsSource = rows;
        AccountHint.Text = rows.Count == 0
            ? "还没有保存的账号。点「自动读取」或手动填写密钥后保存。"
            : "「连接」切换账号；铅笔图标编辑昵称、密钥与数据目录。所有密钥以 DPAPI 加密保存，可长期复用。";
    }

    private async void AccountConnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountRow row }) return;
        KeyStatus.Text = $"正在连接 {row.DisplayName} …";
        try
        {
            var message = await Task.Run(() => AppState.Instance.OpenWeChatData(row.WxId));
            KeyStatus.Text = message;
            RefreshAccounts();
        }
        catch (Exception ex)
        {
            KeyStatus.Text = $"连接失败：{ex.Message}";
        }
    }

    private async void AccountEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountRow row }) return;
        var state = AppState.Instance;
        var account = state.KeyVault.ListAccounts().FirstOrDefault(a => a.WxId == row.WxId);
        if (account is null) return;

        var nameBox = new TextBox
        {
            Header = "昵称（界面显示用，留空则用联系人库解析）",
            Text = account.NickName ?? "",
            PlaceholderText = "昵称"
        };
        var keyBox = new TextBox
        {
            Header = "dbKey（64 位 hex，留空则不修改）",
            PlaceholderText = "48b985c8…",
            Text = ""
        };
        var pathBox = new TextBox
        {
            Header = "微信数据目录（留空 = 自动定位）",
            Text = account.DataPath ?? "",
            PlaceholderText = @"C:\Users\你\Documents\xwechat_files\账号目录",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var browse = new Button
        {
            Content = "浏览…",
            Height = 32,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        browse.Click += async (_, _) =>
        {
            var picked = await PickFolderAsync();
            if (picked is not null) pathBox.Text = picked;
        };
        var clearPath = new Button
        {
            Content = "清除",
            Height = 32,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ToolTipService.SetToolTip(clearPath, "清除自定义目录，恢复自动定位");
        clearPath.Click += (_, _) => pathBox.Text = "";
        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(clearPath, 2);
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(clearPath);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(keyBox);
        panel.Children.Add(pathRow);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"编辑「{row.DisplayName}」",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = nameBox.Text.Trim();
        var newPath = string.IsNullOrWhiteSpace(pathBox.Text) ? null : pathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(newName)) state.KeyVault.SetNickName(row.WxId, newName);
        var newKey = keyBox.Text.Trim();
        var keyChanged = false;
        if (newKey.Length > 0)
        {
            if (!state.KeyVault.SetDbKey(row.WxId, newKey))
            {
                _ = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "dbKey 格式不对",
                    Content = "需要 64 位十六进制字符串（32 字节）。",
                    CloseButtonText = "知道了"
                }.ShowAsync();
                return;
            }

            keyChanged = true;
        }

        var pathChanged = !string.Equals(account.DataPath, newPath, StringComparison.OrdinalIgnoreCase);
        state.KeyVault.SetDataPath(row.WxId, newPath);
        if ((pathChanged || keyChanged) && string.Equals(row.WxId, state.Account?.WxId, StringComparison.Ordinal))
            try
            {
                KeyStatus.Text = await Task.Run(() => state.OpenWeChatData(row.WxId));
            }
            catch (Exception ex)
            {
                KeyStatus.Text = $"重新连接失败：{ex.Message}";
            }

        RefreshAccounts();
    }

    private async void AccountDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountRow row }) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除账号",
            Content = $"将从保管库移除「{row.DisplayName}」的密钥与目录设置。微信数据本身不受影响，下次使用需重新读取密钥。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        var state = AppState.Instance;
        state.KeyVault.Delete(row.WxId);
        if (string.Equals(row.WxId, state.Account?.WxId, StringComparison.Ordinal)) state.CloseWeChatData();
        RefreshAccounts();
    }

    /// <summary>手动填写密钥时选择数据目录。</summary>
    private async void BrowseDataPath_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (path is not null) ManualDataPath.Text = path;
    }

    /// <summary>选目录（WinUI 3 需要显式绑定窗口句柄）。</summary>
    private async Task<string?> PickFolderAsync()
    {
        var window = App.MainAppWindow;
        if (window is null) return null;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(window));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 中首项 IsSelected="True" 会在解析期触发本事件，此时各分区元素尚未创建，
        // 必须判空（否则点开设置页直接闪退）
        if (TabList is null || PaneKeys is null) return;
        if (TabList.SelectedItem is not ListViewItem item) return;
        PaneKeys.Visibility = VisibleIf(item.Tag?.ToString() == "keys");
        PaneModels.Visibility = VisibleIf(item.Tag?.ToString() == "models");
        PaneVoice.Visibility = VisibleIf(item.Tag?.ToString() == "voice");
        PanePrivacy.Visibility = VisibleIf(item.Tag?.ToString() == "privacy");
        PaneAbout.Visibility = VisibleIf(item.Tag?.ToString() == "about");
    }

    private static Visibility VisibleIf(bool b)
    {
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>模型状态文案（实查文件，下载完成后立即反映）。</summary>
    private void ShowModelStatus()
    {
        var status = AppState.Instance.Judge.Status();
        LayaModelStatus.Text = status.Ready
            ? $"模型已就绪（{status.Present}/{status.Total} 文件）：{status.Directory}——判读立即可用。"
            : $"模型未下载完整（{status.Present}/{status.Total} 文件），点「下载模型」补齐缺失文件。";
    }

    // —— 密钥 ——

    private async void AutoRead_Click(object sender, RoutedEventArgs e)
    {
        AutoReadButton.IsEnabled = false;
        KeyStatus.Text = "正在扫描微信进程内存（几秒到十几秒）…";
        try
        {
            var accounts = await AppState.Instance.ReadKeysAsync();
            if (accounts.Count == 0)
            {
                KeyStatus.Text = "❌ 未读取到密钥：请确认微信（Weixin.exe）已运行并登录，或手动在下方输入。";
                return;
            }

            var first = accounts[0];
            ManualWxId.Text = first.WxId;
            ManualDbKey.Text = first.DbKey;
            ManualDataPath.Text = first.DataPath ?? "";
            KeyStatus.Text = "✅ 已读取并保存：" +
                             string.Join("；", accounts.Select(a => $"{a.WxId}（{a.NickName}）")) +
                             $"——已回填到输入框（{first.WxId}）。";
        }
        catch (Exception ex)
        {
            KeyStatus.Text = "❌ 密钥读取失败：" + ex.Message +
                             "（可在下方手动输入 wxid 与 dbKey 保存，不影响使用）";
        }
        finally
        {
            AutoReadButton.IsEnabled = true;
        }
    }

    private async void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (!Elevation.TryRestartElevated())
        {
            KeyStatus.Text = "❌ 已取消管理员权限请求。仍可在下方手动输入 wxid 与 dbKey。";
            return;
        }

        if (Application.Current is App app) await app.ShutdownAsync();
    }

    private void ManualSave_Click(object sender, RoutedEventArgs e)
    {
        var wxId = ManualWxId.Text.Trim();
        var dbKey = ManualDbKey.Text.Trim();
        if (wxId.Length == 0 || dbKey.Length != 64 || !dbKey.All(Uri.IsHexDigit))
        {
            KeyStatus.Text = "wxid 不能为空，dbKey 必须是 64 位 hex。";
            return;
        }

        AppState.Instance.KeyVault.SaveManual(wxId, dbKey,
            string.IsNullOrWhiteSpace(ManualDataPath.Text) ? null : ManualDataPath.Text.Trim());
        KeyStatus.Text = $"已保存 {wxId} 的手动密钥。";
    }

    // —— 本地 laya ——

    private async void DownloadLaya_Click(object sender, RoutedEventArgs e)
    {
        // 模型下载经 Core 的 LayaModel；下载完成后状态立即反映（引擎失败不缓存，首次判读会重试）
        LayaModelStatus.Text = "正在从 HuggingFace 下载 laya-multilingual 权重（约 700MB）…";
        DownloadLayaButton.IsEnabled = false;
        CancelLayaButton.IsEnabled = true;
        _layaDownloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Percent is { } pct) LayaProgress.Value = pct;
                LayaModelStatus.Text = $"{p.FileName}: {p.BytesReceived / 1024 / 1024}MB" +
                                       (p.TotalBytes is { } t ? $" / {t / 1024 / 1024}MB" : "") +
                                       (p.SpeedBytesPerSecond is { } sp && sp > 0 ? $"（{FormatSize(sp)}/s）" : "");
            });
            await AppState.Instance.Judge.Model.EnsureAsync(progress, _layaDownloadCts.Token);
            RefreshAccounts();
            ShowModelStatus();
        }
        catch (OperationCanceledException)
        {
            LayaModelStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            LayaModelStatus.Text = $"下载失败：{ex.Message}";
        }
        finally
        {
            DownloadLayaButton.IsEnabled = true;
            CancelLayaButton.IsEnabled = false;
        }
    }

    private async void OpenVoiceModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(AppState.Instance.Transcriber.ModelFiles.ModelPath)!;
        Directory.CreateDirectory(dir);
        await Launcher.LaunchFolderPathAsync(dir);
    }

    private async void OpenLayaModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = AppState.Instance.Judge.Model.Directory;
        Directory.CreateDirectory(dir);
        await Launcher.LaunchFolderPathAsync(dir);
    }

    private static string FormatSize(double bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / 1024 / 1024 / 1024:0.0}GB",
            >= 1024 * 1024 => $"{bytes / 1024 / 1024:0.0}MB",
            >= 1024 => $"{bytes / 1024:0}KB",
            _ => $"{bytes:0}B"
        };
    }

    private void CancelLaya_Click(object sender, RoutedEventArgs e)
    {
        _layaDownloadCts?.Cancel();
    }

    private async void DownloadVoice_Click(object sender, RoutedEventArgs e)
    {
        DownloadVoiceButton.IsEnabled = false;
        _voiceDownloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Percent is { } pct) VoiceProgress.Value = pct;
                VoiceModelStatus.Text = $"{p.FileName}: {p.BytesReceived / 1024 / 1024}MB" +
                                        (p.SpeedBytesPerSecond is { } sp && sp > 0 ? $"（{FormatSize(sp)}/s）" : "");
            });
            await AppState.Instance.Transcriber.DownloadModelAsync(_voiceDownloadCts.Token, progress);
            VoiceModelStatus.Text = $"模型已就绪：{AppState.Instance.Transcriber.ModelFiles.ModelPath}";
        }
        catch (OperationCanceledException)
        {
            VoiceModelStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            VoiceModelStatus.Text = $"下载失败：{ex.Message}";
        }
        finally
        {
            DownloadVoiceButton.IsEnabled = true;
        }
    }

    // —— 隐私与写回 ——

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (state.WriteBack is not { } writeBack)
        {
            WriteBackListText.Text = "请先在「对话与监听」页打开微信数据。";
            return;
        }

        var committed = state.Ledger.ListByState(WriteState.Committed);
        if (committed.Count == 0)
        {
            WriteBackListText.Text = "当前没有生效中的写回。";
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "还原到修改前",
            Content = $"将按台账把 {committed.Count} 条被改写的消息逐条还原成原文（行级 SQL，" +
                      "不覆盖文件、不丢弃之后收到的新消息）。是否继续？",
            PrimaryButtonText = "还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var results = writeBack.RestoreAll();
            var ok = results.Count(r => r.Error is null);
            var failed = results.Where(r => r.Error is not null).ToList();
            WriteBackListText.Text = failed.Count == 0
                ? $"已还原 {ok} 条写回，微信库回到本应用修改之前。"
                : $"已还原 {ok} 条；{failed.Count} 条失败（台账保留，可稍后重试）：\n" +
                  string.Join("\n", failed.Select(f =>
                      $"· {f.Entry.SessionId}#{f.Entry.AnnotatedLocalId}：{f.Error}"));
        }
        catch (Exception ex)
        {
            WriteBackListText.Text = $"还原失败：{ex.Message}";
        }
    }

    private void ListWriteBacks_Click(object sender, RoutedEventArgs e)
    {
        var entries = AppState.Instance.Ledger.RecentWriteBacks();
        WriteBackListText.Text = entries.Count == 0
            ? "暂无写回记录。"
            : string.Join("\n", entries.Select(en =>
                $"{en.WrittenAt:MM-dd HH:mm} {en.WxId} {en.DbFileName}/{en.TableName} 行{en.AnnotatedLocalId} " +
                $"[{WriteStateLabel.Detail(en.State, en.RevokedAt)}]"));
    }

    // —— 保存 ——

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Instance.Settings;
        s.VoiceEnabled = VoiceSwitch.IsOn;
        s.VoiceModelDir = VoiceModelDirBox.Text.Trim();
        // 监听配置热生效：重启监听（已打开数据时）
        if (s.MonitorEnabled && AppState.Instance.Account is not null) AppState.Instance.StartMonitor();
        s.AutoWriteEnabled = WriteBackSwitch.IsOn;
        AppState.Instance.SaveSettings();
        SaveHint.Text = "已保存 " + DateTimeOffset.Now.ToString("HH:mm:ss");
    }

    /// <summary>账号行：头像 + 昵称（不显示 wxid）+ 数据目录 + 连接/目录/删除。</summary>
    private sealed class AccountRow
    {
        private static readonly SolidColorBrush[] Palette =
        [
            new(Color.FromArgb(255, 79, 107, 237)),
            new(Color.FromArgb(255, 47, 163, 107)),
            new(Color.FromArgb(255, 194, 65, 12)),
            new(Color.FromArgb(255, 124, 58, 237))
        ];

        public AccountRow(string wxId, string name, string path, bool isCurrent, string? avatarPath)
        {
            WxId = wxId;
            DisplayName = name;
            DataPath = path;
            IsCurrent = isCurrent;
            AvatarBrush = Palette[Math.Abs(wxId.GetHashCode()) % Palette.Length];
            if (avatarPath is not null && File.Exists(avatarPath)) AvatarImage = new BitmapImage(new Uri(avatarPath));
        }

        public string WxId { get; }

        public string DisplayName { get; }

        public string DataPath { get; }

        public bool IsCurrent { get; }

        public Brush AvatarBrush { get; }

        public ImageSource? AvatarImage { get; }

        public Visibility AvatarVisibility => AvatarImage is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility InitialVisibility => AvatarImage is null ? Visibility.Visible : Visibility.Collapsed;

        public string Initial => DisplayName.Length > 0 ? DisplayName[..1] : "?";

        public string Subtitle => IsCurrent ? $"已连接 · {MaskedPath}" : MaskedPath;

        /// <summary>路径里含 wxid，展示时打码（本软件界面不展示 wxid）。</summary>
        private string MaskedPath =>
            Regex.Replace(
                DataPath, @"wxid_[^\\/]+", "wxid_***", RegexOptions.IgnoreCase);

        public string ActionLabel => "连接";
    }
}
