using Microsoft.UI.Xaml.Controls;

namespace JevChatAnalyzer.App.Views;

/// <summary>
/// WinUI 同一时刻只允许一个 ContentDialog；并发调用会抛「Only a single ContentDialog can be open at any time」。
/// 统一走这里排队，避免判读结果、确认框、记录列表互相打断。
/// </summary>
internal static class ContentDialogExtensions
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowSerializedAsync(this ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // 页面已卸载或 XamlRoot 失效时不再弹框，调用方按「取消」处理。
            return ContentDialogResult.None;
        }
        finally
        {
            Gate.Release();
        }
    }
}
