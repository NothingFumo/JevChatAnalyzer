using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JevChatAnalyzer.Core.History;
using JevChatAnalyzer.App.Services;

namespace JevChatAnalyzer.App.Views;

/// <summary>判读历史页面（AES-GCM 加密存储的本地记录）。</summary>
public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        var records = AppState.Instance.History.Recent(200);
        HistoryList.ItemsSource = records.Select(r => new HistoryRow(r)).ToList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryRow row)
        {
            AppState.Instance.History.Delete(row.Record.Id);
            Load();
        }
    }

    private sealed record HistoryRow(HistoryRecord Record)
    {
        public string Title => $"{Record.SessionTitle} · {Record.FocusText}";
        public string Summary => Record.Verdict.Summary;
        public string Meta => $"{Record.CreatedAt:yyyy-MM-dd HH:mm} · {Record.Verdict.Model}" +
                              (Record.WriteBackId is null ? "" : $" · 已写回 {Record.WriteBackId}");
    }
}
