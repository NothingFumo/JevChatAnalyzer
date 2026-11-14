using System.ComponentModel;
using JevChatAnalyzer.App.Services;
using JevChatAnalyzer.Core.Analysis;
using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Ledger;
using JevChatAnalyzer.Core.Messages;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace JevChatAnalyzer.App.Views;

/// <summary>
///     对话与监听：会话列表（头像 / 监听开关 / 分析 / 记录）+ 判读结果卡片。
///     本页是常驻主页面；「会话判读」独立页已删除。不展示微信消息原文。
/// </summary>
public sealed partial class SessionsPage : Page
{
    private readonly bool _suppressSettingsWrite;
    private ChatSession? _currentSession;
    private AnalysisOutcome? _lastOutcome;
    private bool _analysisRunning;

    // ── 会话列表 + 监听开关 ─────────────────────────────────────────

    private List<SessionRow> _rows = [];
    private bool _selectionUpdating;
    private IReadOnlyList<ChatSession> _sessions = [];

    public SessionsPage()
    {
        InitializeComponent();
        var state = AppState.Instance;
        _suppressSettingsWrite = true; // 初始化写控件会触发 Toggled/ValueChanged
        MonitorSwitch.IsOn = state.Settings.MonitorEnabled;
        IntervalBox.SelectedIndex = state.Settings.MonitorIntervalSeconds switch
        {
            <= 2 => 0,
            <= 5 => 1,
            <= 10 => 2,
            <= 30 => 3,
            _ => 4
        };
        // 旧值（10/20/80）归一到新档位，避免下拉显示与实际值不一致
        var turns = state.Settings.ContextTurns;
        if (turns != 0 && turns is not (30 or 50 or 100))
        {
            turns = turns < 30 ? 30 : turns < 50 ? 50 : turns < 100 ? 100 : 0;
            state.Settings.ContextTurns = turns;
            state.SaveSettings();
        }

        ContextTurnsBox.SelectedIndex = turns switch
        {
            0 => 3, // 全部
            <= 30 => 0,
            <= 50 => 1,
            _ => 2
        };
        _suppressSettingsWrite = false;
        // 共享管线：手动分析与实时监听的分析结果都汇到这里刷新卡片
        state.Pipeline.Analyzed += outcome => DispatcherQueue.TryEnqueue(() => ShowOutcome(outcome));
        state.Flow.Wrote += results => DispatcherQueue.TryEnqueue(() =>
        {
            var ok = results.Where(r => r.Error is null).ToList();
            var failed = results.Where(r => r.Error is not null).ToList();
            if (ok.Count > 0)
            {
                SetStatus($"自动写回完成：{ok.Count} 条引用消息已写入微信数据库" +
                          $"（{string.Join("、", ok.Select(r => r.Entry!.SessionId))}）。" +
                          "启动微信即可看到；可随时「撤销最近写回」。", InfoBarSeverity.Success);
                RevokeButton.IsEnabled = true;
            }

            if (failed.Count > 0)
                SetStatus($"自动写回有 {failed.Count} 条失败（保留在队列中）：\n" +
                          string.Join("\n", failed.Select(r => r.Error)), InfoBarSeverity.Warning);
        });
        state.Flow.StatusChanged += st => DispatcherQueue.TryEnqueue(() =>
        {
            if (!Status.IsOpen) SetStatus(st.Message);
        });
        // 启动自动连接的结果（后台线程）回流到界面
        state.StatusChanged += message => DispatcherQueue.TryEnqueue(() => SetStatus(message));
        state.ConnectionChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateConnectionState();
            if (state.IsConnected) RefreshSessions();
        });
        Loaded += (_, _) =>
        {
            UpdateConnectionState();
            if (!state.IsConnected) return;
            RefreshSessions();
        };
    }

    private List<SessionRow> VisibleRows =>
        (SessionList.ItemsSource as IEnumerable<SessionRow>)?.ToList() ?? _rows;

    /// <summary>顶部身份：自己的头像 + 昵称（不展示 wxid）。</summary>
    private void UpdateConnectionState()
    {
        var state = AppState.Instance;
        if (state.IsConnected)
        {
            var name = string.IsNullOrWhiteSpace(state.Account?.DisplayName) ? "当前账号" : state.Account!.DisplayName;
            SelfName.Text = name;
            SelfInitial.Text = name.Length > 0 ? name[..1] : "?";
            var avatar = state.Account?.AvatarPath;
            if (avatar is not null)
            {
                SelfImage.Source = new BitmapImage(new Uri(avatar));
                SelfImage.Visibility = Visibility.Visible;
                SelfInitial.Visibility = Visibility.Collapsed;
            }
            else
            {
                SelfImage.Visibility = Visibility.Collapsed;
                SelfInitial.Visibility = Visibility.Visible;
            }

            SelfHint.Text = "已连接";
        }
        else
        {
            SelfName.Text = "未连接";
            SelfInitial.Text = "?";
            SelfImage.Visibility = Visibility.Collapsed;
            SelfInitial.Visibility = Visibility.Visible;
            SelfHint.Text = "未连接微信数据";
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("正在连接微信数据…");
        var message = await Task.Run(() => AppState.Instance.TryAutoConnect());
        SetStatus(message, AppState.Instance.IsConnected ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        UpdateConnectionState();
        if (AppState.Instance.IsConnected) RefreshSessions();
    }

    private void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Status.Message = message;
        Status.Severity = severity;
        Status.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;

        RefreshButton.IsEnabled = !busy && AppState.Instance.Account is not null;
        AnalyzeButton.IsEnabled = !busy && _currentSession is not null && AppState.Instance.Account is not null;
        DraftButton.IsEnabled = !busy && _lastOutcome is not null;
        RevokeButton.IsEnabled = !busy && AppState.Instance.Ledger.ListByState(WriteState.Committed).Count > 0;
        ReapplyButton.IsEnabled = !busy && AppState.Instance.Ledger.ListByState(WriteState.Revoked)
            .Any(en => en.QuoteText is not null && en.AnnotatedLocalId is not null);
    }

    private void ShowOutcome(AnalysisOutcome outcome)
    {
        _lastOutcome = outcome;
    }

    /// <summary>判读结果用对话框呈现（右侧常驻面板已移除，避免大片空白）。</summary>
    private async Task ShowVerdictAsync(AnalysisOutcome outcome)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = outcome.Verdict.Summary,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var spec in JudgeProtocol.Specs)
        {
            if (!outcome.Verdict.Answers.TryGetValue(spec.Id, out var answer)) continue;
            panel.Children.Add(new TextBlock
            {
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Text = $"{spec.Instructions}  {answer}"
            });
        }

        var kind = outcome.Session.Kind == SessionKind.Group ? "群聊" : "单聊";
        panel.Children.Add(new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = $"「{outcome.Session.DisplayName}」 · {kind} · {outcome.Verdict.Model} · " +
                   $"{outcome.Verdict.JudgedAt:MM-dd HH:mm}" +
                   (outcome.Enqueued ? " · 已写回引用块" : "")
        });

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "判读结果",
            Content = new ScrollViewer { MaxHeight = 420, Content = panel },
            CloseButtonText = "关闭"
        }.ShowSerializedAsync();
    }

    /// <summary>页面已开启导航缓存（订阅只注册一次），每次进入时刷新会话与状态。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshSessions();
    }

    private void RefreshSessions()
    {
        var state = AppState.Instance;
        var chat = state.Account?.Chat;
        if (chat is null)
        {
            _rows = [];
            _sessions = [];
            SessionList.ItemsSource = null;
            SessionList.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyHint.Text = "尚未连接微信数据。应用启动会自动连接；如果一直失败，请先到「设置 → 微信密钥」读取密钥后重试。";
            SummaryText.Text = "";
            SelectedText.Text = "";
            return;
        }

        SessionList.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;

        var selected = _rows.Where(r => r.IsSelected).Select(r => r.Session.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        var whitelist = SessionWhitelist.Parse(state.Settings.MonitorSessions);
        // 只保留群聊与单聊：公众号/系统/未知类型不参与判读与监听
        _sessions = chat.ListSessions()
            .Where(x => x.Kind is SessionKind.Group or SessionKind.Single)
            .ToList();
        _rows = _sessions.Select(x => new SessionRow(x,
            state.Settings.MonitorAll || whitelist.Contains(x.SessionId),
            selected.Contains(x.SessionId))).ToList();
        ApplyFilter();
        RefreshButton.IsEnabled = true;
        UpdateConnectionState();

        if (_currentSession is null && _rows.Count > 0) SessionList.SelectedIndex = 0; // 自动选中最近活跃会话
    }

    private void ApplyFilter()
    {
        if (SessionList is null || SessionSearchBox is null || KindBox is null ||
            SelectedText is null || SummaryText is null || SelectAllBox is null)
            return; // XAML 解析期 SelectedIndex 会先触发一次，此时控件尚未建完
        var keyword = SessionSearchBox.Text?.Trim() ?? "";
        var kind = KindBox.SelectedIndex switch
        {
            1 => SessionKind.Group,
            2 => SessionKind.Single,
            _ => (SessionKind?)null
        };
        var rows = _rows.AsEnumerable();
        if (kind is { } k) rows = rows.Where(r => r.Session.Kind == k);
        if (keyword.Length > 0)
            rows = rows.Where(r =>
                r.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.Session.SessionId.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        SessionList.ItemsSource = rows.ToList();
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        var state = AppState.Instance;
        SelectedText.Text = $"已选 {_rows.Count(r => r.IsSelected)} 个";
        SummaryText.Text = state.Settings.MonitorAll
            ? $"全部 {_rows.Count} 个会话"
            : $"{_rows.Count(r => r.Monitored)}/{_rows.Count} 个会话";
        var visible = VisibleRows;
        _selectionUpdating = true;
        SelectAllBox.IsChecked = visible.Count > 0 && visible.All(r => r.IsSelected);
        _selectionUpdating = false;
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyFilter();
    }

    private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_selectionUpdating) return;
        var target = SelectAllBox.IsChecked == true;
        foreach (var row in VisibleRows) row.IsSelected = target;
        UpdateCounters();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
        UpdateCounters();
    }

    private void RowSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: SessionRow row } box)
        {
            row.IsSelected = box.IsChecked == true;
            UpdateCounters();
        }
    }

    /// <summary>行尾胶囊：单个会话开/关实时监听。</summary>
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SessionRow row }) SetMonitored([row], !row.Monitored);
    }

    private void EnableSelected_Click(object sender, RoutedEventArgs e)
    {
        SetMonitored(_rows.Where(r => r.IsSelected), true);
    }

    private void DisableSelected_Click(object sender, RoutedEventArgs e)
    {
        SetMonitored(_rows.Where(r => r.IsSelected), false);
    }

    /// <summary>单个/批量开关监听：统一落到 MonitorAll + 白名单两个字段。</summary>
    private void SetMonitored(IEnumerable<SessionRow> rows, bool monitored)
    {
        var targets = rows.ToList();
        if (targets.Count == 0)
        {
            SelectedText.Text = "先勾选左侧的会话";
            return;
        }

        var state = AppState.Instance;
        var allIds = _rows.Select(r => r.Session.SessionId).ToHashSet(StringComparer.Ordinal);
        var set = state.Settings.MonitorAll
            ? new HashSet<string>(allIds, StringComparer.Ordinal) // 「全部」状态下展开成显式清单
            : SessionWhitelist.Parse(state.Settings.MonitorSessions);
        foreach (var row in targets)
            if (monitored)
                set.Add(row.Session.SessionId);
            else
                set.Remove(row.Session.SessionId);

        // 覆盖当前全部会话即回到「全部」
        if (allIds.Count > 0 && allIds.All(set.Contains))
        {
            state.Settings.MonitorAll = true;
            state.Settings.MonitorSessions = "";
        }
        else
        {
            state.Settings.MonitorAll = false;
            state.Settings.MonitorSessions = string.Join(",", set);
        }

        state.SaveSettings();
        state.RestartMonitor();
        RefreshSessions();
    }

    private async void AnalyzeRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionRow row }) return;
        SessionList.SelectedItem = row;
        _currentSession = row.Session;
        await AnalyzeCurrentAsync();
    }

    private void RecordsRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionRow row }) return;
        var views = AppState.Instance.Ledger.Recent(500)
            .Where(v => v.Record.SessionId == row.Session.SessionId)
            .Take(50)
            .ToList();
        var text = views.Count == 0
            ? "这个会话还没有判读记录。"
            : string.Join("\n", views.Select(v =>
                $"{v.Record.CreatedAt:MM-dd HH:mm} · {Truncate(v.Record.Verdict.Summary, 60)}{StateLabel(v.WriteBack)}"));
        _ = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"「{row.DisplayName}」的判读记录",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 }
            },
            CloseButtonText = "关闭"
        }.ShowSerializedAsync();
    }

    private static string StateLabel(WriteBackEntry? entry)
    {
        return entry is null ? "" : $" [{WriteStateLabel.Of(entry.State)}]";
    }

    /// <summary>上下文条数快捷选择（判读与实时监听共用，立即保存）。</summary>
    private void ContextTurnsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContextTurnsBox.SelectedItem is not ComboBoxItem item) return;
        var n = item.Content?.ToString()?.Contains("全部") == true
            ? 0 // 0 = 不设上限（单次最多 2000 条，见 AnalysisPipeline）
            : int.TryParse(item.Content?.ToString()?.Replace("上下文", "").Replace("条", "").Trim(), out var parsed)
                ? parsed
                : -1;
        if (n >= 0 && AppState.Instance.Settings.ContextTurns != n)
        {
            AppState.Instance.Settings.ContextTurns = n;
            AppState.Instance.SaveSettings();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshSessions();
    }

    // ── 监听设置（已并入本页，设置页不再单列） ──────────────────────

    private void MonitorSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsWrite) return;
        var state = AppState.Instance;
        state.Settings.MonitorEnabled = MonitorSwitch.IsOn;
        // 首次开启时若没有白名单，默认监听全部活跃会话；否则开关打开却一个都不听，等于空转。
        if (MonitorSwitch.IsOn && !state.Settings.MonitorAll
            && SessionWhitelist.Parse(state.Settings.MonitorSessions).Count == 0)
        {
            state.Settings.MonitorAll = true;
        }

        state.SaveSettings();
        state.RestartMonitor();
        RefreshSessions();
        SetStatus(MonitorSwitch.IsOn ? "实时监听已开启。" : "实时监听已关闭。",
            MonitorSwitch.IsOn ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsWrite || IntervalBox.SelectedItem is not ComboBoxItem item) return;
        var text = item.Content?.ToString()?.Replace("秒", "").Trim();
        if (!int.TryParse(text, out var seconds) || seconds < 2) return;
        var state = AppState.Instance;
        if (state.Settings.MonitorIntervalSeconds == seconds) return;
        state.Settings.MonitorIntervalSeconds = seconds;
        state.SaveSettings();
        state.RestartMonitor();
    }

    private void Session_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRow row) return;
        if (_currentSession?.SessionId != row.Session.SessionId) _lastOutcome = null; // 切换会话后起草按钮重新等待本次判读
        _currentSession = row.Session;
        SetBusy(false);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        await AnalyzeCurrentAsync();
    }

    private async Task AnalyzeCurrentAsync()
    {
        if (_analysisRunning) return; // 连点行内分析/主按钮时不并发判读，避免并发对话框
        var state = AppState.Instance;
        if (_currentSession is null || state.Account is null) return;
        _analysisRunning = true;
        SetBusy(true);
        SetStatus($"正在分析 [{_currentSession.DisplayName}] …");
        try
        {
            var outcome = await state.Pipeline.AnalyzeAsync(_currentSession, true);
            if (outcome is null)
            {
                SetStatus("该会话中没有找到可分析的对方消息（可能全是自己发言、系统消息，或语音未启用转写）。",
                    InfoBarSeverity.Warning);
                return;
            }

            if (outcome.Verdict.IsBlindDraft)
            {
                SetStatus("本地 laya 不可用，已盲起草兜底（未经判读校准，未写回数据库）。" +
                          "候选见「起草回复」。", InfoBarSeverity.Warning);
                return;
            }

            SetStatus(outcome.Enqueued
                ? "判读完成——已写进对方那条消息的引用块（若会话里暂未显示，切换/重开会话即可；写入失败会自动排队重试）。"
                : "判读完成（自动写回已在设置中关闭）。", InfoBarSeverity.Success);
            await ShowVerdictAsync(outcome);
        }
        catch (Exception ex)
        {
            SetStatus($"判读失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _analysisRunning = false;
            SetBusy(false);
        }
    }

    private async void Draft_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (_lastOutcome is null || _currentSession is null) return;

        if (!state.Settings.DraftEnabled)
        {
            // 直接跳设置，页面上的开关与密钥提示本身就是说明，不必先弹一条看不到的 InfoBar。
            Frame.Navigate(typeof(SettingsPage), "draft");
            return;
        }

        var keyEnv = string.IsNullOrWhiteSpace(state.Settings.DraftApiKeyEnv)
            ? "LLM_API_KEY"
            : state.Settings.DraftApiKeyEnv;
        var apiKey = Environment.GetEnvironmentVariable(keyEnv);
        if (string.IsNullOrEmpty(apiKey))
        {
            Frame.Navigate(typeof(SettingsPage), "draft");
            return;
        }

        SetBusy(true);
        try
        {
            var options = new DraftOptions
            {
                Endpoint = new Uri(state.Settings.DraftEndpoint),
                ApiKey = apiKey,
                Model = state.Settings.DraftModel,
                CandidateCount = 3
            };
            using var client = new DraftClient(options);
            // ContextTurns = 0 表示「全部上下文」，与判读路径一致（-1 = 不限）
            var messages = state.Account!.Chat.ReadSessionMessages(_currentSession.SessionId,
                state.Settings.ContextTurns <= 0 ? -1 : state.Settings.ContextTurns + 5);
            var request = JudgeProtocol.BuildRequest(
                messages, _lastOutcome.Focus, _currentSession.DisplayName, _currentSession.Kind == SessionKind.Group,
                state.Account?.WxId);
            var suggestions = await client.DraftAsync(request, _lastOutcome.Verdict);
            // 候选重排序（三段式第三段）：判读引擎选出最合适的一条置顶
            try
            {
                suggestions = await state.Judge.RerankAsync(request, suggestions);
            }
            catch
            {
                // 重排序失败保持原顺序（候选仍可用）
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                SelectedIndex = 0,
                MaxHeight = 360,
                ItemsSource = suggestions.Select(s => new DraftRow(s.Tone, s.Text)).ToList()
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "候选回复",
                PrimaryButtonText = "粘贴到微信",
                SecondaryButtonText = "复制",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
                Content = list
            };
            var picked = await dialog.ShowSerializedAsync();
            if (list.SelectedItem is not DraftRow row) return;
            if (picked == ContentDialogResult.Primary)
            {
                await PasteToWeChatAsync(_currentSession.DisplayName, row.Text);
            }
            else if (picked == ContentDialogResult.Secondary)
            {
                var copied = WeChatPaster.CopyToClipboard(row.Text);
                SetStatus(copied ? "已复制到剪贴板。" : "复制到剪贴板失败。",
                    copied ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"起草失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Reapply_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        var entry = state.Ledger.ListByState(WriteState.Revoked)
            .FirstOrDefault(en => en.QuoteText is not null && en.AnnotatedLocalId is not null);
        if (entry is null)
        {
            SetStatus("没有可再次写回的判读结果（需要先写过并撤销过一次）。");
            return;
        }

        SetBusy(true);
        try
        {
            if (state.WriteBack is not { } writeBack)
            {
                SetStatus("请先在「对话与监听」页打开微信数据。", InfoBarSeverity.Warning);
                return;
            }

            var written = writeBack.Reapply(entry);
            SetStatus($"已再次写回（判读：{Truncate(written.QuoteText, 40)}）。可在「撤销」中还原。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"再次写回失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    ///     粘贴到微信：先复制，倒计时留给用户切到目标会话，确认前台是微信后发送一次 Ctrl+V。
    ///     前台不是微信就不粘贴；永不发送 Enter。
    /// </summary>
    private async Task PasteToWeChatAsync(string expectedTitle, string text)
    {
        if (!WeChatPaster.CopyToClipboard(text))
        {
            SetStatus("复制到剪贴板失败。", InfoBarSeverity.Error);
            return;
        }

        for (var i = 3; i >= 1; i--)
        {
            SetStatus($"{i} 秒后粘贴到「{expectedTitle}」——请现在切到微信并打开该会话。");
            await Task.Delay(1000);
        }

        if (!WeChatPaster.IsWeChatForeground(out var hwnd))
        {
            SetStatus("微信不在前台，未粘贴。文本已在剪贴板，可手动 Ctrl+V。", InfoBarSeverity.Warning);
            return;
        }

        if (!WeChatPaster.SendPasteShortcut(hwnd))
        {
            SetStatus("粘贴快捷键发送失败，请手动 Ctrl+V。", InfoBarSeverity.Error);
            return;
        }

        SetStatus("已粘贴到微信输入框（未发送，确认内容后再按 Enter）。", InfoBarSeverity.Success);
    }

    private static string Truncate(string? s, int n)
    {
        return string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        var entry = state.Ledger.ListByState(WriteState.Committed).FirstOrDefault();
        if (entry is null)
        {
            SetStatus("当前没有可撤销的写回记录。");
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤销写回",
            Content =
                $"将把 {entry.WrittenAt:MM-dd HH:mm} 加上引用块的那条消息还原成原文（{entry.SessionId}），并删除对应判读行。微信库回到本应用修改之前的状态。是否继续？",
            PrimaryButtonText = "撤销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowSerializedAsync() != ContentDialogResult.Primary) return;
        SetBusy(true);
        try
        {
            if (state.WriteBack is not { } writeBack)
            {
                SetStatus("请先在「对话与监听」页打开微信数据。", InfoBarSeverity.Warning);
                return;
            }

            var changed = writeBack.Revoke(entry);
            // 撤销后允许再次分析/写回同一条消息
            if (entry.AnnotatedLocalId is { } localId) state.Pipeline.Forget(entry.SessionId, localId);
            SetStatus($"已撤销（还原 {changed} 条消息）。可以再次分析该会话重新写回。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"撤销失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Pending_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        var pending = state.Ledger.ListByState(WriteState.Pending);

        string NameOf(string sessionId)
        {
            return state.Account?.Chat.ListSessions(300).FirstOrDefault(s => s.SessionId == sessionId)?.DisplayName
                   ?? "未命名会话";
        }

        var text = pending.Count == 0
            ? "待写队列为空。分析「对方最新消息」后会自动入队并实时落库。"
            : string.Join("\n", pending.Select(p =>
                $"{p.WrittenAt:MM-dd HH:mm}「{NameOf(p.SessionId)}」待写 1 条" +
                (p.LastError is null ? "" : $"（上轮：{Truncate(p.LastError, 40)}）")));
        _ = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"待写队列（{pending.Count}）",
            Content = new ScrollViewer
                { MaxHeight = 320, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = "关闭"
        }.ShowSerializedAsync();
    }

    private void Status_Close(object sender, object e)
    {
        Status.IsOpen = false;
    }

    private sealed record DraftRow(string Tone, string Text)
    {
        public override string ToString()
        {
            return $"[{Tone}] {Text}";
        }
    }

    /// <summary>会话行：勾选 + 头像 + 名称 + 统计 + 监听胶囊 + 分析/记录入口。</summary>
    private sealed class SessionRow : INotifyPropertyChanged
    {
        private readonly SessionAvatar _avatar;
        private bool _isSelected;
        private bool _monitored;

        public SessionRow(ChatSession session, bool monitored, bool selected)
        {
            Session = session;
            _monitored = monitored;
            _isSelected = selected;
            _avatar = SessionAvatar.Load(session.SessionId,
                string.IsNullOrWhiteSpace(session.DisplayName) ? session.SessionId : session.DisplayName);
        }

        public ChatSession Session { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                Raise(nameof(IsSelected));
            }
        }

        public bool Monitored
        {
            get => _monitored;
            set
            {
                if (_monitored == value) return;
                _monitored = value;
                Raise(nameof(Monitored));
                Raise(nameof(ToggleLabel));
                Raise(nameof(ToggleForeground));
                Raise(nameof(ToggleBorderBrush));
            }
        }

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Session.DisplayName) ? Session.SessionId : Session.DisplayName;

        public ImageSource? AvatarImage => _avatar.Image;

        public Visibility AvatarVisibility => _avatar.AvatarVisibility;

        public Visibility InitialVisibility => _avatar.InitialVisibility;

        public string Initial => _avatar.Initial;

        public Brush AvatarBrush => _avatar.Brush;

        public string KindLabel => Session.Kind switch
        {
            SessionKind.Group => "群聊",
            SessionKind.Single => "单聊",
            SessionKind.Official => "公众号",
            _ => "会话"
        };

        public string Subtitle =>
            $"{KindLabel} · {Session.MessageCount} 条 · 最近 {Session.LastTime:MM-dd HH:mm}";

        public string ToggleLabel => Monitored ? "监听中" : "监听";

        public Brush ToggleForeground => Monitored
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        public Brush ToggleBorderBrush => Monitored
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private sealed record VerdictAnswerRow(string Label, string Value);
}
