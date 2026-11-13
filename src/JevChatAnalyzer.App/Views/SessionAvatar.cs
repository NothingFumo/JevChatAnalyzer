using Windows.UI;
using JevChatAnalyzer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace JevChatAnalyzer.App.Views;

/// <summary>
///     会话头像视图模型：能取到微信缓存头像就用图片，否则回退到首字色块。
///     头像来自 <c>db_storage/head_image/head_image.db</c>，由 <see cref="AvatarStore" /> 落成本地文件。
/// </summary>
public sealed class SessionAvatar
{
    private static readonly SolidColorBrush[] Palette =
    [
        new(Color.FromArgb(255, 79, 107, 237)),
        new(Color.FromArgb(255, 47, 163, 107)),
        new(Color.FromArgb(255, 194, 65, 12)),
        new(Color.FromArgb(255, 124, 58, 237)),
        new(Color.FromArgb(255, 14, 116, 144)),
        new(Color.FromArgb(255, 180, 83, 9))
    ];

    private SessionAvatar(ImageSource? image, Brush brush, string initial)
    {
        Image = image;
        Brush = brush;
        Initial = initial;
    }

    public ImageSource? Image { get; }

    public Brush Brush { get; }

    public string Initial { get; }

    public Visibility AvatarVisibility => Image is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility InitialVisibility => Image is null ? Visibility.Visible : Visibility.Collapsed;

    public static SessionAvatar Load(string sessionId, string displayName, int decodePixels = 72)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? sessionId : displayName;
        var initial = name.Length > 0 ? name[..1] : "?";
        var brush = Palette[Math.Abs(sessionId.GetHashCode()) % Palette.Length];

        var path = AppState.Instance.Account?.Avatars.GetAvatarPath(sessionId);
        if (path is null) return new SessionAvatar(null, brush, initial);
        try
        {
            var image = new BitmapImage
            {
                DecodePixelWidth = decodePixels,
                UriSource = new Uri(path)
            };
            return new SessionAvatar(image, brush, initial);
        }
        catch (Exception)
        {
            // 图片损坏时退回首字
            return new SessionAvatar(null, brush, initial);
        }
    }
}
