using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Viddl.App;

/// <summary>Version, credits and contact details.</summary>
public sealed class AboutDialog : ContentDialog
{
    public const string DiscordUser = "dustersevensix";
    private const string DiscordUrl = "https://discord.com/app";

    public AboutDialog(XamlRoot root, string dataFolder)
    {
        XamlRoot = root;
        Title = "About viddl";
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;

        var panel = new StackPanel { Spacing = 14, MaxWidth = 460 };

        // Header: icon, name and version
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 40 });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "viddl", FontSize = 24, FontWeight = FontWeights.SemiBold });
        name.Children.Add(new TextBlock { Text = $"Version {AppVersion}", Opacity = 0.7 });
        header.Children.Add(name);
        panel.Children.Add(header);

        panel.Children.Add(Paragraph(
            "Save online videos as MP4. Play a video in the built-in browser, pick the stream viddl detects, " +
            "and it downloads and converts it without re-encoding."));

        // Contact
        panel.Children.Add(Heading("Contact"));
        var discord = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        discord.Children.Add(new TextBlock { Text = "Discord:", VerticalAlignment = VerticalAlignment.Center });
        discord.Children.Add(new TextBlock
        {
            Text = DiscordUser, FontWeight = FontWeights.SemiBold, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        });
        var copy = new Button { Content = "Copy", Padding = new Thickness(10, 3, 10, 3) };
        ToolTipService.SetToolTip(copy, "Copy the Discord username");
        copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(DiscordUser);
            Clipboard.SetContent(data);
            copy.Content = "Copied";
        };
        discord.Children.Add(copy);
        var open = new HyperlinkButton { Content = "Open Discord", NavigateUri = new Uri(DiscordUrl) };
        ToolTipService.SetToolTip(open, "Open Discord, then add or message " + DiscordUser);
        discord.Children.Add(open);
        panel.Children.Add(discord);

        // Credits
        panel.Children.Add(Heading("Built with"));
        var credits = new StackPanel { Spacing = 0 };
        foreach (var (text, url) in new[]
        {
            ("ffmpeg - converting and muxing", "https://ffmpeg.org/"),
            ("yt-dlp - extracting videos from web pages", "https://github.com/yt-dlp/yt-dlp"),
            ("Microsoft Edge WebView2 - the built-in browser", "https://developer.microsoft.com/microsoft-edge/webview2/"),
            ("Windows App SDK and WinUI 3", "https://learn.microsoft.com/windows/apps/windows-app-sdk/"),
            ("Block lists: StevenBlack hosts, URLhaus, NoCoin", "https://github.com/StevenBlack/hosts"),
        })
        {
            credits.Children.Add(new HyperlinkButton { Content = text, NavigateUri = new Uri(url), Padding = new Thickness(0, 2, 0, 2) });
        }
        panel.Children.Add(credits);

        var folder = new HyperlinkButton { Content = "Open viddl's data folder", Padding = new Thickness(0, 2, 0, 2) };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(dataFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataFolder}\"") { UseShellExecute = true });
        };
        panel.Children.Add(folder);

        panel.Children.Add(new TextBlock
        {
            Text = "Only download videos you have the right to save. DRM-protected streams are not supported.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7,
        });

        Content = new ScrollViewer { Content = panel };
    }

    /// <summary>"2.0.0" from the assembly version.</summary>
    public static string AppVersion
    {
        get
        {
            var v = typeof(AboutDialog).Assembly.GetName().Version;
            return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, -6) };

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
}
