using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace MeetingCapture.TrayApp;

/// <summary>About dialog: version, author links, license and third-party credits.</summary>
internal static class AboutDialog
{
    private static bool _open;

    internal static async Task ShowAsync(XamlRoot xamlRoot)
    {
        // Prevent overlapping dialogs when the user triggers About more than once.
        if (_open) return;
        _open = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = $"About {AboutInfo.AppName}",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                Content = BuildContent(),
            };
            await dialog.ShowAsync();
        }
        // Reset the guard even if dialog creation or display fails.
        finally { _open = false; }
    }

    private static UIElement BuildContent()
    {
        // Build this as a small tree of WinUI controls so links and long license lists remain usable.
        var panel = new StackPanel { Spacing = 10, MaxWidth = 420 };
        panel.Children.Add(new TextBlock { Text = AboutInfo.Description, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {AboutInfo.Version()}",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Created by {AboutInfo.Author}. {AboutInfo.Copyright}.",
            TextWrapping = TextWrapping.Wrap,
        });

        var social = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var link in AboutInfo.SocialLinks)
            social.Children.Add(new HyperlinkButton { Content = link.Label, NavigateUri = new Uri(link.Url) });
        panel.Children.Add(social);

        panel.Children.Add(new TextBlock
        {
            Text = "Credits and third-party licenses",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 6, 0, 0),
        });
        var credits = new StackPanel { Spacing = 4 };
        foreach (var credit in AboutInfo.Credits)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var link = new Hyperlink { NavigateUri = new Uri(credit.Url) };
            link.Inlines.Add(new Run { Text = credit.Name });
            text.Inlines.Add(link);
            text.Inlines.Add(new Run { Text = $" - {credit.License}" });
            credits.Children.Add(text);
        }
        panel.Children.Add(new ScrollViewer { MaxHeight = 220, Content = credits });
        panel.Children.Add(new TextBlock
        {
            Text = "Full notices are in THIRD-PARTY-NOTICES.md in the source repository.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        return panel;
    }
}
