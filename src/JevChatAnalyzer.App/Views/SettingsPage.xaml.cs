using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using JevChatAnalyzer.App.Services;
using JevChatAnalyzer.Core.Judge.NlayaJudge;
using JevChatAnalyzer.Core.Net;
using JevChatAnalyzer.Core.Keys;
using JevChatAnalyzer.Core.Voice;

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
    }

    private void LoadSettings()
    {
        var s = AppState.Instance.Settings;
        (s.JudgeBackend switch
        {
            "local" => BackendLocal,
            "本地 HTTP 服务r" => BackendLayaServer,
            _ => BackendCloud,
        }).IsChecked = true;
        CloudEndpointBox.Text = s.CloudEndpoint;
        CloudModelBox.Text = s.CloudModel;
        CloudSanitizeSwitch.IsOn = s.CloudSanitize;
        LayaServerBox.Text = s.LayaServerUrl;
        VoiceSwitch.IsOn = s.VoiceEnabled;
        VoiceModelDirBox.Text = s.VoiceModelDir;
        MonitorSwitch.IsOn = s.MonitorEnabled;
        MonitorIntervalBox.Text = s.MonitorIntervalSeconds.ToString();
        ContextTurnsBox.Text = s.ContextTurns.ToString();
        MonitorSessionsBox.Text = s.MonitorSessions;
        WriteBackSwitch.IsOn = s.AutoWriteEnabled;

        var transcriber = AppState.Instance.Transcriber;
        VoiceModelStatus.Text = transcriber.IsModelReady
            ? $"模型已就绪：{transcriber.ModelFiles.ModelPath}（{FormatSize(new FileInfo(transcriber.ModelFiles.ModelPath).Length)}）"
            : "模型未下载。";
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 中首项 IsSelected="True" 会在解析期触发本事件，此时各分区元素尚未创建，
        // 必须判空（否则点开设置页直接闪退）
        if (TabList is null || PaneKeys is null || PaneJudge is null || PaneDialog is null ||
            PaneModels is null || PaneVoice is null || PanePrivacy is null || PaneAbout is null)
        {
            return;
        }
        if (TabList.SelectedItem is not ListViewItem item)
        {
            return;
        }
        PaneKeys.Visibility = VisibleIf(item.Tag?.ToString() == "keys");
        PaneJudge.Visibility = VisibleIf(item.Tag?.ToString() == "judge");
        PaneDialog.Visibility = VisibleIf(item.Tag?.ToString() == "dialog");
        PaneModels.Visibility = VisibleIf(item.Tag?.ToString() == "models");
        PaneVoice.Visibility = VisibleIf(item.Tag?.ToString() == "voice");
        PanePrivacy.Visibility = VisibleIf(item.Tag?.ToString() == "privacy");
        PaneAbout.Visibility = VisibleIf(item.Tag?.ToString() == "about");
    }

    private static Visibility VisibleIf(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

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
            // 回显到输入框：首个账号的 wxid / dbKey / 数据目录
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

    // —— 判读后端 ——

    private async void DownloadLaya_Click(object sender, RoutedEventArgs e)
    {
        // laya 模型下载经 NlayaEngine（Core），未就绪时提示
        LayaModelStatus.Text = "正在从 HuggingFace 下载 laya-multilingual 权重（约 700MB）…";
        DownloadLayaButton.IsEnabled = false;
        CancelLayaButton.IsEnabled = true;
        _layaDownloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Percent is { } pct)
                {
                    LayaProgress.Value = pct;
                }
                LayaModelStatus.Text = $"{p.FileName}: {p.BytesReceived / 1024 / 1024}MB" +
                                       (p.TotalBytes is { } t ? $" / {t / 1024 / 1024}MB" : "") +
                                       (p.SpeedBytesPerSecond is { } sp && sp > 0 ? $"（{FormatSize(sp)}/s）" : "");
            });
            var dir = await NlayaModelDownloader.DownloadAsync(
                AppState.Instance.Settings.NlayaModelDir, progress, _layaDownloadCts.Token);
            LayaModelStatus.Text = $"模型已就绪：{dir}";
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
        var dir = System.IO.Path.GetDirectoryName(AppState.Instance.Transcriber.ModelFiles.ModelPath)!;
        System.IO.Directory.CreateDirectory(dir);
        await Windows.System.Launcher.LaunchFolderPathAsync(dir);
    }

    private async void OpenLayaModelDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = NlayaModelDownloader.ResolveDirectory(AppState.Instance.Settings.NlayaModelDir);
        System.IO.Directory.CreateDirectory(dir);
        await Windows.System.Launcher.LaunchFolderPathAsync(dir);
    }

    private static string FormatSize(double bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1024 / 1024 / 1024:0.0}GB",
        >= 1024 * 1024 => $"{bytes / 1024 / 1024:0.0}MB",
        >= 1024 => $"{bytes / 1024:0}KB",
        _ => $"{bytes:0}B",
    };

    private void CancelLaya_Click(object sender, RoutedEventArgs e) => _layaDownloadCts?.Cancel();

    private async void DownloadVoice_Click(object sender, RoutedEventArgs e)
    {
        DownloadVoiceButton.IsEnabled = false;
        _voiceDownloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Percent is { } pct)
                {
                    VoiceProgress.Value = pct;
                }
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

    private async void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JevChatAnalyzer", "backups");
        System.IO.Directory.CreateDirectory(dir);
        await Windows.System.Launcher.LaunchFolderPathAsync(dir);
    }

    private void ListWriteBacks_Click(object sender, RoutedEventArgs e)
    {
        var entries = AppState.Instance.WriteBack.ListEntries();
        WriteBackListText.Text = entries.Count == 0
            ? "暂无写回记录。"
            : string.Join("\n", entries.Select(en =>
                $"{en.WrittenAt:MM-dd HH:mm} {en.WxId} {en.DbFileName}/{en.TableName} 行{string.Join(",", en.InsertedLocalIds)}" +
                (en.RevokedAt is null ? " [生效中]" : $" [已撤销 {en.RevokedAt:MM-dd HH:mm}]")));
    }

    // —— 保存 ——

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Instance.Settings;
        s.JudgeBackend = BackendLocal.IsChecked == true ? "local"
            : BackendLayaServer.IsChecked == true ? "本地 HTTP 服务r"
            : "cloud";
        s.CloudEndpoint = CloudEndpointBox.Text.Trim();
        s.CloudModel = CloudModelBox.Text.Trim();
        s.CloudSanitize = CloudSanitizeSwitch.IsOn;
        s.LayaServerUrl = LayaServerBox.Text.Trim();
        s.VoiceEnabled = VoiceSwitch.IsOn;
        s.MonitorEnabled = MonitorSwitch.IsOn;
        s.MonitorIntervalSeconds = Math.Max(2, int.TryParse(MonitorIntervalBox.Text.Trim(), out var iv) ? iv : 5);
        s.ContextTurns = Math.Clamp(int.TryParse(ContextTurnsBox.Text.Trim(), out var ctx) ? ctx : 20, 1, 200);
        s.MonitorSessions = MonitorSessionsBox.Text.Trim();
        s.VoiceModelDir = VoiceModelDirBox.Text.Trim();
        // 监听配置热生效：重启监听（已打开数据时）
        if (s.MonitorEnabled && AppState.Instance.Chat is not null)
        {
            AppState.Instance.StartMonitor();
        }
        s.AutoWriteEnabled = WriteBackSwitch.IsOn;
        AppState.Instance.SaveSettings();
        SaveHint.Text = "已保存 " + DateTimeOffset.Now.ToString("HH:mm:ss");
    }
}
