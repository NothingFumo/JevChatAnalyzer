using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JevChatAnalyzer.Core.History;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.WriteBack;
using JevChatAnalyzer.App.Services;
using JevChatAnalyzer.Core.Voice;
using JevChatAnalyzer.Core.Draft;

namespace JevChatAnalyzer.App.Views;

/// <summary>会话判读主页面：会话列表 → 消息时间线 → 判读卡片 → 写回/撤销。</summary>
public sealed partial class ChatPage : Page
{
    private IReadOnlyList<ChatSession> _sessions = [];
    private IReadOnlyList<ChatMessage> _messages = [];
    private ChatSession? _currentSession;
    private ChatMessage? _focusMessage;
    private JudgeVerdict? _lastVerdict;

    public ChatPage()
    {
        InitializeComponent();
        // 自动写回完成回调：从后台线程切回 UI 线程展示结果
        AppState.Instance.AutoWriteCompleted += results =>
        {
            DispatcherQueue.TryEnqueue(() =>
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
        };
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
        var hasFocus = !busy && _focusMessage is not null;
        JudgeButton.IsEnabled = hasFocus;
        TranscribeButton.IsEnabled = hasFocus && _focusMessage.LocalType == ChatStore.TypeVoice
                                     && AppState.Instance.Transcriber.IsModelReady
                                     && AppState.Instance.CurrentAccountDir is not null;
        DraftButton.IsEnabled = hasFocus && _lastVerdict is not null && AppState.Instance.Settings.DraftEnabled;
        RevokeButton.IsEnabled = !busy && AppState.Instance.WriteBack.ListEntries().Any(en => en.RevokedAt is null);
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
        if (SessionList.SelectedItem is not SessionRow row)
        {
            return;
        }
        _currentSession = row.Session;
        LoadMessages();
    }

    private void LoadMessages()
    {
        var chat = AppState.Instance.Chat;
        if (chat is null || _currentSession is null)
        {
            return;
        }
        try
        {
            _messages = chat.ReadSessionMessages(_currentSession.SessionId, limit: 300);
            MessageList.ItemsSource = _messages
                .Where(m => m.DisplayText.Length > 0)
                .Select(m => new MessageRow(m))
                .ToList();
            if (MessageList.Items.Count > 0)
            {
                MessageList.SelectedIndex = MessageList.Items.Count - 1;
                MessageScroller.UpdateLayout();
                MessageScroller.ScrollToVerticalOffset(MessageScroller.ScrollableHeight);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"读取消息失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageRow row)
        {
            _focusMessage = row.Message;
        }
        SetBusy(false);
    }

    private async void TranscribeButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (_focusMessage is null || state.CurrentAccountDir is null)
        {
            return;
        }
        SetBusy(true);
        try
        {
            var silk = VoiceLocator.FindVoiceData(
                state.CurrentAccountDir, state.CurrentDbKey!,
                _focusMessage.ServerId, _focusMessage.SenderWxId,
                _focusMessage.CreateTime.ToUnixTimeSeconds(), _focusMessage.LocalId);
            if (silk is null)
            {
                SetStatus("媒体库中未找到该语音的数据。", InfoBarSeverity.Warning);
                return;
            }
            var cacheKey = VoiceTranscriber.CacheKey(_focusMessage.ServerId,
                _focusMessage.CreateTime.ToUnixTimeSeconds(), _focusMessage.LocalId);
            var text = state.Transcriber.Transcribe(silk, cacheKey);
            SetStatus($"转写完成：{text}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"转写失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DraftButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (_focusMessage is null || _currentSession is null || _lastVerdict is null)
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
            var request = JudgeService.BuildRequest(
                _messages, _focusMessage, _currentSession.DisplayName, _currentSession.Kind == SessionKind.Group);
            var suggestions = await client.DraftAsync(request, _lastVerdict);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "候选回复",
                PrimaryButtonText = "关闭",
            };
            var panel = new StackPanel { Spacing = 8 };
            foreach (var s in suggestions)
            {
                panel.Children.Add(new StackPanel { Spacing = 2 });
                var item = (StackPanel)panel.Children[^1];
                item.Children.Add(new TextBlock
                {
                    Text = $"[{s.Tone}]",
                    FontSize = 12,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.Colors.Gray),
                });
                item.Children.Add(new TextBlock { Text = s.Text, TextWrapping = TextWrapping.Wrap });
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

    private async void JudgeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMessage is null || _currentSession is null)
        {
            return;
        }
        var state = AppState.Instance;
        SetBusy(true);
        SetStatus($"正在通过 {state.Settings.JudgeBackend} 后端判读 …");
        try
        {
            var request = JudgeService.BuildRequest(
                _messages, _focusMessage, _currentSession.DisplayName, _currentSession.Kind == SessionKind.Group);
            var verdict = await state.Judge.JudgeAsync(request);

            _lastVerdict = verdict;
            VerdictSummary.Text = verdict.Summary;
            VerdictAnswers.ItemsSource = verdict.Answers
                .Join(JudgeQuestions.Specs, a => a.Key, s => s.Id,
                    (a, s) => new VerdictAnswerRow(s.Instructions, a.Value.ToString()))
                .ToList();
            VerdictMeta.Text = $"{verdict.Model} · {verdict.JudgedAt:HH:mm:ss}";
            VerdictPanel.Visibility = Visibility.Visible;

            state.History.Add(new HistoryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                WxId = state.CurrentWxId ?? "",
                SessionId = _currentSession.SessionId,
                SessionTitle = _currentSession.DisplayName,
                FocusText = _focusMessage.DisplayText,
                MessageKey = VoiceTranscriber.CacheKey(_focusMessage.ServerId,
                    _focusMessage.CreateTime.ToUnixTimeSeconds(), _focusMessage.LocalId),
                Verdict = verdict,
                CreatedAt = DateTimeOffset.Now,
            });

            // 自动写回：判读「对方消息」后入队，微信退出后由监视器落库
            if (state.Settings.AutoWriteEnabled)
            {
                if (_focusMessage.IsMine)
                {
                    SetStatus("判读完成。自动写回仅针对对方发送的消息（本条是我发的，跳过）。",
                        InfoBarSeverity.Informational);
                }
                else
                {
                    state.WriteBack.EnqueuePending(state.CurrentWxId!, _currentSession.SessionId,
                        _focusMessage, verdict, quoteText: verdict.Summary);
                    state.EnsureAutoWriteWatch();
                    SetStatus("判读完成，已加入待写队列——微信完全退出后将自动写入为引用消息" +
                              "（引用对方这条消息，可随时撤销/还原）。", InfoBarSeverity.Success);
                }
            }
            else
            {
                SetStatus("判读完成（自动写回已在设置中关闭）。", InfoBarSeverity.Success);
            }
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

    /// <summary>待写队列视图（入队/落库进度透明可查）。</summary>
    private void PendingButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = AppState.Instance.WriteBack.ListPending();
        var text = pending.Count == 0
            ? "待写队列为空。判读「对方消息」后会自动入队，微信退出时落库。"
            : string.Join("\n", pending.Select(p =>
                $"{p.QueuedAt:MM-dd HH:mm} {p.SessionId} 引用「{p.FocusMessage.DisplayText}」"));
        _ = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"待写队列（{pending.Count}）",
            Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = "关闭",
        }.ShowAsync();
    }

    private async void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        // 撤销对象 = 台账中最近一条生效中的写回
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

    private void Status_Close(object sender, object e) => Status.IsOpen = false;

    // —— 列表行视图模型 ——

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

    private sealed record MessageRow(ChatMessage Message)
    {
        public string Sender => Message.IsMine ? "我" : Message.SenderDisplayName;
        public string TimeLabel => Message.CreateTime.ToString("MM-dd HH:mm");
        public string Text => Message.DisplayText;
        public string QuoteText => Message.Quote is { } q ? $"{q.FromDisplayName}：{q.Content}" : "";
        public Microsoft.UI.Xaml.Visibility QuoteVisibility =>
            Message.Quote is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }

    private sealed record VerdictAnswerRow(string Label, string Value);
}
