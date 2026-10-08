using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.App.Services;
using JevChatAnalyzer.Core.Draft;
using JevChatAnalyzer.Core.Voice;

namespace JevChatAnalyzer.App.Views;

/// <summary>
/// 分析工作台：会话列表（仅名称与统计）→ 一键分析 / 实时监听自动分析 → 判读卡片 → 自动写回队列。
/// 不展示微信消息原文。手动分析与实时监听共用 AnalysisPipeline 单一路径。
/// </summary>
public sealed partial class AnalyzePage : Page
{
    private IReadOnlyList<ChatSession> _sessions = [];
    private ChatSession? _currentSession;
    private AnalysisOutcome? _lastOutcome;

    public AnalyzePage()
    {
        InitializeComponent();
        var state = AppState.Instance;
        // 共享管线：手动分析与实时监听的分析结果都汇到这里刷新卡片
        state.Pipeline.Analyzed += outcome => DispatcherQueue.TryEnqueue(() => ShowOutcome(outcome));
        state.AutoWriteCompleted += results => DispatcherQueue.TryEnqueue(() =>
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
            {
                SetStatus($"自动写回有 {failed.Count} 条失败（保留在队列中）：\n" +
                          string.Join("\n", failed.Select(r => r.Error)), InfoBarSeverity.Warning);
            }
        });
        state.MonitorStatusChanged += st => DispatcherQueue.TryEnqueue(() =>
        {
            if (Status.IsOpen != true)
            {
                SetStatus(st.Message, InfoBarSeverity.Informational);
            }
        });
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
        OpenDataButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy && AppState.Instance.Chat is not null;
        AnalyzeButton.IsEnabled = !busy && _currentSession is not null && AppState.Instance.Chat is not null;
        DraftButton.IsEnabled = !busy && _lastOutcome is not null && AppState.Instance.Settings.DraftEnabled;
        RevokeButton.IsEnabled = !busy && AppState.Instance.WriteBack.ListEntries().Any(en => en.RevokedAt is null);
    }

    private void ShowOutcome(AnalysisOutcome outcome)
    {
        _lastOutcome = outcome;
        FocusInfo.Message = $"「{outcome.Focus.SenderDisplayName}」：{outcome.Focus.DisplayText}";
        FocusInfo.Severity = InfoBarSeverity.Informational;
        FocusInfo.IsOpen = true;

        VerdictSummary.Text = outcome.Verdict.Summary;
        VerdictAnswers.ItemsSource = outcome.Verdict.Answers
            .Join(JudgeQuestions.Specs, a => a.Key, s => s.Id,
                (a, s) => new VerdictAnswerRow(s.Instructions, a.Value.ToString()))
            .ToList();
        VerdictMeta.Text = $"{outcome.Verdict.Model} · {outcome.Verdict.JudgedAt:HH:mm:ss}" +
                           (outcome.Enqueued ? " · 已入待写队列" : "");
        VerdictPanel.Visibility = Visibility.Visible;
        SetBusy(false);
    }

    private async void OpenDataButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var state = AppState.Instance;
            string message;
            if (state.KeyVault.GetCurrent() is null)
            {
                SetStatus("保管库中没有密钥，正在通过 rwk.dll 自动读取（需要微信已登录运行）…");
                var accounts = await state.ReadKeysAsync();
                if (accounts.Count == 0)
                {
                    SetStatus("未读取到密钥：请确认微信已登录，或到「设置 → 微信密钥」手动输入。", InfoBarSeverity.Warning);
                    return;
                }
                message = $"已自动读取 {accounts.Count} 个账号的密钥。";
            }
            else
            {
                message = "";
            }
            message += state.OpenWeChatData();
            SetStatus(message, InfoBarSeverity.Success);
            RefreshSessions();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshSessions()
    {
        var chat = AppState.Instance.Chat;
        if (chat is null)
        {
            return;
        }
        _sessions = chat.ListSessions(200);
        SessionList.ItemsSource = _sessions.Select(s => new SessionRow(s)).ToList();
        RefreshButton.IsEnabled = true;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshSessions();

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionRow row)
        {
            _currentSession = row.Session;
        }
        SetBusy(false);
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (_currentSession is null || state.Chat is null)
        {
            return;
        }
        SetBusy(true);
        SetStatus($"正在分析 [{_currentSession.DisplayName}] …");
        try
        {
            var outcome = await state.Pipeline.AnalyzeAsync(state, _currentSession, enqueue: true);
            if (outcome is null)
            {
                SetStatus("该会话中没有找到可分析的对方消息（可能全是自己发言、系统消息，或语音未启用转写）。",
                    InfoBarSeverity.Warning);
                return;
            }
            SetStatus(outcome.Enqueued
                ? "判读完成，已加入待写队列——微信完全退出后将自动写入为引用消息（可随时撤销/还原）。"
                : "判读完成（自动写回已在设置中关闭）。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"判读失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DraftButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (_lastOutcome is null || _currentSession is null)
        {
            return;
        }
        SetBusy(true);
        try
        {
            var options = new DraftOptions
            {
                Endpoint = new Uri(state.Settings.DraftEndpoint),
                ApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY"),
                Model = state.Settings.DraftModel,
                CandidateCount = 3,
            };
            var client = new DraftClient(options);
            var messages = state.Chat!.ReadSessionMessages(_currentSession.SessionId,
                limit: state.Settings.ContextTurns + 5);
            var request = JudgeService.BuildRequest(
                messages, _lastOutcome.Focus, _currentSession.DisplayName, _currentSession.Kind == SessionKind.Group);
            var suggestions = await client.DraftAsync(request, _lastOutcome.Verdict);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "候选回复",
                PrimaryButtonText = "关闭",
            };
            var panel = new StackPanel { Spacing = 8 };
            foreach (var s in suggestions)
            {
                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(new TextBlock
                {
                    Text = $"[{s.Tone}]",
                    FontSize = 12,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                });
                item.Children.Add(new TextBlock { Text = s.Text, TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(item);
            }
            dialog.Content = new ScrollViewer { MaxHeight = 360, Content = panel };
            await dialog.ShowAsync();
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

    private async void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        var entry = state.WriteBack.ListEntries().LastOrDefault(en => en.RevokedAt is null);
        if (entry is null)
        {
            SetStatus("当前没有可撤销的写回记录。", InfoBarSeverity.Informational);
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤销写回",
            Content = $"将删除 {entry.WrittenAt:MM-dd HH:mm} 写入的引用消息（{entry.SessionId}，" +
                      "不影响你之后的新消息）。备份文件仍保留，可整库还原。是否继续？",
            PrimaryButtonText = "撤销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        SetBusy(true);
        try
        {
            var deleted = state.WriteBack.Revoke(entry, state.CurrentDbKey!);
            SetStatus($"已撤销（删除 {deleted} 行）。备份仍在，可在设置中整库还原。", InfoBarSeverity.Success);
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

    private void PendingButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = AppState.Instance.WriteBack.ListPending();
        var text = pending.Count == 0
            ? "待写队列为空。分析「对方最新消息」后会自动入队，微信退出时落库。"
            : string.Join("\n", pending.Select(p =>
                $"{p.QueuedAt:MM-dd HH:mm} {p.SessionId} 待写 1 条"));
        _ = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"待写队列（{pending.Count}）",
            Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = "关闭",
        }.ShowAsync();
    }

    private void Status_Close(object sender, object e) => Status.IsOpen = false;

    private sealed record SessionRow(ChatSession Session)
    {
        public string DisplayName => Session.DisplayName;
        public string KindLabel => Session.Kind switch
        {
            SessionKind.Group => "群聊",
            SessionKind.Single => "单聊",
            SessionKind.Official => "公众号",
            _ => "会话",
        };
        public string CountLabel => $"{Session.MessageCount} 条 · {Session.LastTime:MM-dd HH:mm}";
    }

    private sealed record VerdictAnswerRow(string Label, string Value);
}
