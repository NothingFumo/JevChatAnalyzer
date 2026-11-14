using JevChatAnalyzer.App.Services;
using JevChatAnalyzer.Core.Judge;
using JevChatAnalyzer.Core.Ledger;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace JevChatAnalyzer.App.Views;

/// <summary>
///     判读历史：按会话名 / 判读摘要搜索，按写回状态筛选；行内可看七题明细、删除记录。
///     只展示判读结论，不展示微信消息原文。
/// </summary>
public sealed partial class HistoryPage : Page
{
    private List<HistoryRow> _rows = [];

    public HistoryPage()
    {
        InitializeComponent();
        Load();
    }

    /// <summary>判读历史页有导航缓存：每次进入都重新读台账，避免显示过期列表。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Load();
    }

    private void Load()
    {
        _rows = AppState.Instance.Ledger.Recent(400)
            .Select(v => new HistoryRow(v))
            .ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (HistoryList is null || SearchBox is null || StateBox is null ||
            CountText is null) return; // XAML 解析期 SelectedIndex 会先触发一次
        var keyword = SearchBox.Text?.Trim() ?? "";
        var state = StateBox.SelectedIndex switch
        {
            1 => (WriteState?)WriteState.Committed,
            2 => WriteState.Revoked,
            3 => WriteState.Pending,
            4 => WriteState.Rejected,
            _ => null
        };

        IEnumerable<HistoryRow> rows = _rows;
        if (state is { } s) rows = rows.Where(r => r.State == s);
        if (keyword.Length > 0)
            rows = rows.Where(r =>
                r.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var list = rows.ToList();
        HistoryList.ItemsSource = list;
        CountText.Text = $"{list.Count} / {_rows.Count} 条";
        EmptyPanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Filter_Changed(object sender, object e)
    {
        ApplyFilter();
    }

    private void Filter_Changed(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyFilter();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Load();
    }

    /// <summary>从会话页「记录」或判读结果跳转：按会话名过滤。</summary>
    public void FilterByTitle(string title)
    {
        SearchBox.Text = title;
        ApplyFilter();
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryRow row } || row.View.WriteBack is not { } entry) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "撤销这条写回",
            Content = $"将把 {entry.WrittenAt:MM-dd HH:mm} 对「{row.Record.SessionTitle}」的写回还原成原文。" +
                      "微信库回到本应用修改之前的状态。是否继续？",
            PrimaryButtonText = "撤销",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowSerializedAsync() != ContentDialogResult.Primary) return;

        if (AppState.Instance.WriteBack is not { } writeBack)
        {
            await ShowMessageAsync("无法撤销", "请先在「会话与监听」页打开微信数据。");
            return;
        }

        try
        {
            writeBack.Revoke(entry);
            if (entry.AnnotatedLocalId is { } localId)
                AppState.Instance.Pipeline.Forget(entry.SessionId, localId);
            Load();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("撤销失败", ex.Message);
        }
    }

    private async void Reapply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryRow row } || row.View.WriteBack is not { } entry) return;
        if (AppState.Instance.WriteBack is not { } writeBack)
        {
            await ShowMessageAsync("无法写回", "请先在「会话与监听」页打开微信数据。");
            return;
        }

        try
        {
            writeBack.Reapply(entry);
            Load();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("再次写回失败", ex.Message);
        }
    }

    private Task<ContentDialogResult> ShowMessageAsync(string title, string content)
    {
        return new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "知道了"
        }.ShowSerializedAsync();
    }

    private async void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryRow row }) return;
        var record = row.Record;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = record.Verdict.Summary,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var spec in JudgeProtocol.Specs)
        {
            if (!record.Verdict.Answers.TryGetValue(spec.Id, out var answer)) continue;
            panel.Children.Add(new TextBlock
            {
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Text = $"{spec.Instructions}  {answer}"
            });
        }

        panel.Children.Add(new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = $"{row.Meta} · {row.StateLabel}"
        });

        _ = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"「{record.SessionTitle}」的判读明细",
            Content = new ScrollViewer { MaxHeight = 420, Content = panel },
            CloseButtonText = "关闭"
        }.ShowSerializedAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryRow row }) return;
        var entry = row.View.WriteBack;
        var committed = entry?.State == WriteState.Committed;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = committed ? "撤销并删除判读记录" : "删除判读记录",
            Content = $"将删除 {row.Record.CreatedAt:MM-dd HH:mm} 对「{row.Record.SessionTitle}」的判读记录" +
                      (committed ? "，并先撤销写回、把消息还原成原文。" : "。") +
                      "（不影响其他记录）",
            PrimaryButtonText = committed ? "撤销并删除" : "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowSerializedAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (committed)
            {
                if (AppState.Instance.WriteBack is not { } writeBack)
                {
                    await ShowMessageAsync("无法删除", "已写回的记录需要连接微信数据后才能撤销，请先在「会话与监听」页打开微信数据。");
                    return;
                }

                writeBack.Revoke(entry!);
                if (entry!.AnnotatedLocalId is { } localId)
                    AppState.Instance.Pipeline.Forget(entry.SessionId, localId);
            }

            AppState.Instance.Ledger.DeleteJudgment(row.Record.Id);
            Load();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法删除", ex.Message);
        }
    }

    /// <summary>按会话批量彻底删除：已写回的条目先撤销，再删记录。</summary>
    private async void DeleteBySession_Click(object sender, RoutedEventArgs e)
    {
        var groups = _rows
            .GroupBy(r => r.Record.SessionId)
            .Select(g => new SessionGroup(
                g.Key,
                AppState.Instance.SessionName(g.Key, g.First().Record.SessionTitle),
                g.Count(),
                g.Count(r => r.View.WriteBack?.State == WriteState.Committed)))
            .OrderByDescending(g => g.Count)
            .ToList();
        if (groups.Count == 0)
        {
            await ShowMessageAsync("没有记录", "当前没有可删除的判读记录。");
            return;
        }

        var combo = new ComboBox
        {
            ItemsSource = groups.Select(g => $"{g.Title}（{g.Count} 条，{g.Committed} 条已写回）").ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var pick = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "按会话删除",
            Content = combo,
            PrimaryButtonText = "删除该会话记录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await pick.ShowSerializedAsync() != ContentDialogResult.Primary) return;

        var group = groups[Math.Max(0, combo.SelectedIndex)];
        var records = _rows.Where(r => r.Record.SessionId == group.SessionId).ToList();
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "彻底删除该会话的判读记录",
            Content = $"将删除「{group.Title}」的 {records.Count} 条判读记录" +
                      (group.Committed > 0
                          ? $"；其中 {group.Committed} 条已写回会先撤销并把消息还原（移除引用块）。"
                          : "。") +
                      "此操作不可恢复。",
            PrimaryButtonText = "彻底删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowSerializedAsync() != ContentDialogResult.Primary) return;

        var errors = new List<string>();
        var revoked = 0;
        var writeBack = AppState.Instance.WriteBack;
        if (group.Committed > 0 && writeBack is null)
        {
            await ShowMessageAsync("无法删除", "已写回的记录需要连接微信数据后才能撤销，请先在「会话与监听」页打开微信数据。");
            return;
        }

        if (writeBack is not null)
            foreach (var entry in records.Select(r => r.View.WriteBack).OfType<WriteBackEntry>()
                         .Where(w => w.State == WriteState.Committed))
                try
                {
                    writeBack.Revoke(entry);
                    revoked++;
                    if (entry.AnnotatedLocalId is { } localId)
                        AppState.Instance.Pipeline.Forget(entry.SessionId, localId);
                }
                catch (Exception ex)
                {
                    errors.Add($"{group.Title}：{ex.Message}");
                }

        var deleted = 0;
        foreach (var record in records)
            try
            {
                AppState.Instance.Ledger.DeleteJudgment(record.Record.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                errors.Add($"{record.Record.SessionTitle}：{ex.Message}");
            }

        Load();
        await ShowMessageAsync("删除完成",
            $"已撤销 {revoked} 条写回，删除 {deleted} 条记录。" +
            (errors.Count == 0
                ? ""
                : $"\n{errors.Distinct().Count()} 条未删除：\n" + string.Join("\n", errors.Distinct().Take(5))));
    }

    private sealed record SessionGroup(string SessionId, string Title, int Count, int Committed);

    private static string Truncate(string? s, int n)
    {
        return string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
    }

    private sealed class HistoryRow
    {
        private readonly SessionAvatar _avatar;

        public HistoryRow(JudgmentView view)
        {
            View = view;
            _avatar = SessionAvatar.Load(view.Record.SessionId, view.Record.SessionTitle);
        }

        public JudgmentView View { get; }

        public JudgmentRecord Record => View.Record;

        public WriteState? State => View.WriteBack?.State;

        /// <summary>只显示会话名：本软件不展示微信消息原文。</summary>
        public string Title => Record.SessionTitle;

        public string Summary => Truncate(Record.Verdict.Summary, 120);

        public string Meta => $"{Record.CreatedAt:yyyy-MM-dd HH:mm} · {Record.Verdict.Model}";

        public string StateLabel => WriteStateLabel.Of(State);

        public Brush StateForeground => State switch
        {
            WriteState.Committed => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
            WriteState.Pending => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            WriteState.Orphaned or WriteState.Rejected =>
                (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        public Brush StateBorderBrush => State switch
        {
            WriteState.Committed => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
            WriteState.Pending => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            WriteState.Orphaned or WriteState.Rejected =>
                (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            _ => (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"]
        };

        public ImageSource? AvatarImage => _avatar.Image;

        public Visibility AvatarVisibility => _avatar.AvatarVisibility;

        public Visibility InitialVisibility => _avatar.InitialVisibility;

        public string Initial => _avatar.Initial;

        public Brush AvatarBrush => _avatar.Brush;

        public Visibility RevokeVisibility =>
            State == WriteState.Committed ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ReapplyVisibility =>
            State == WriteState.Revoked && View.WriteBack?.QuoteText is not null &&
            View.WriteBack.FocusJson is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
    }
}
