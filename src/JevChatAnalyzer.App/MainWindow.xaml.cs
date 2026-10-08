using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using JevChatAnalyzer.App.Views;

namespace JevChatAnalyzer.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentFrame.Navigate(typeof(AnalyzePage));
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }
        var tag = item.Tag?.ToString();
        ContentFrame.Navigate(tag switch
        {
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(AnalyzePage),
        });
    }
}
