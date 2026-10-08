using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JevChatAnalyzer.Core.History;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Messages;
using JevChatAnalyzer.Core.WriteBack;
using JevChatAnalyzer.App.Services;
using JevChatAnalyzer.Core.Voice;

namespace JevChatAnalyzer.App.Views;

/// <summary>会话判读主页面：会话列表 → 消息时间线 → 判读卡片 → 写回/撤销。</summary>
public sealed partial class ChatPage : Page
{
    private IReadOnlyList<ChatSession> _sessions = [];
    private IReadOnlyList<ChatMessage> _messages = [];
    private ChatSession? _currentSession;
    private ChatMessage? _focusMessage;
    private JudgeVerdict? _lastVerdict;
    private WriteBackEntry? _lastWriteBack;

    public ChatPage()
    {
        InitializeComponent();
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
        WriteBackButton.IsEnabled = hasFocus && _lastVerdict is not null && AppState.Instance.Settings.WriteBackEnabled;
        RevokeButton.IsEnabled = !busy && _lastWriteBack is not null && _lastWriteBack.RevokedAt is null;
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
            SetStatus("判读完成。", InfoBarSeverity.Success);
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

    private async void WriteBackButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (_focusMessage is null || _currentSession is null || _lastVerdict is null)
        {
            return;
        }
        if (!WriteBackService.IsWeChatClosed())
        {
            SetStatus("微信正在运行。写回前请先完全退出微信（系统托盘右键 → 退出）。", InfoBarSeverity.Warning);
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认写回",
            Content = $"将在微信数据库中插入一条引用消息（引用被分析的那条消息，正文为判读摘要）。\n\n" +
                      "写入前会自动整库备份，可随时撤销或整库还原。是否继续？",
            PrimaryButtonText = "写入",
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
            var table = _focusMessage.Location.TableName;
            var shard = _focusMessage.Location.ShardPath;
            _lastWriteBack = state.WriteBack.WriteQuoteMessage(
                shard, table, state.CurrentDbKey!, state.CurrentWxId!,
                _currentSession.SessionId, _focusMessage, _lastVerdict,
                quoteText: _lastVerdict.Summary);
            RevokeButton.IsEnabled = true;
            SetStatus($"已写回（{_lastWriteBack.Id}）。启动微信后即可看到；如需撤销请点击「撤销写回」。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"写回失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Instance;
        if (_lastWriteBack is null)
        {
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤销写回",
            Content = "将从微信数据库删除本工具写入的引用消息（不影响你之后的新消息）。写入时的备份文件仍保留，可整库还原。是否继续？",
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
            var deleted = state.WriteBack.Revoke(_lastWriteBack, state.CurrentDbKey!);
            _lastWriteBack = null;
            RevokeButton.IsEnabled = false;
            SetStatus($"已撤销（删除 {deleted} 行）。", InfoBarSeverity.Success);
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
