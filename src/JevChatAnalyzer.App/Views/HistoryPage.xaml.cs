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

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row)
        {
            return;
        }
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除判读记录",
            Content = $"将删除 {row.Record.CreatedAt:MM-dd HH:mm} 对「{row.Record.SessionTitle}」的判读记录（不影响微信数据）。是否继续？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
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
