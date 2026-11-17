using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Viddl.Downloads.Engines;

namespace Viddl.App;

/// <summary>Edits a saved video's tags (title, description, comment, date, genre).</summary>
public sealed class TagsDialog : ContentDialog
{
    private readonly Dictionary<string, TextBox> _boxes = new();

    public TagsDialog(XamlRoot root, string fileName, VideoTags current)
    {
        XamlRoot = root;
        Title = "Edit tags";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
        panel.Children.Add(new TextBlock { Text = fileName, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        foreach (var (key, label) in VideoTags.Fields)
        {
            bool multiLine = key is "description" or "comment";
            var box = new TextBox
            {
                Header = label,
                Text = current[key],
                AcceptsReturn = multiLine,
                TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiLine ? 64 : 0,
            };
            _boxes[key] = box;
            panel.Children.Add(box);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Leave a field empty to remove that tag. The video itself isn't re-encoded.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        Content = new ScrollViewer { Content = panel, MaxHeight = 520 };
    }

    /// <summary>The tags as entered.</summary>
    public VideoTags Tags
    {
        get
        {
            var tags = new VideoTags();
            foreach (var (key, box) in _boxes) tags[key] = box.Text;
            return tags;
        }
    }
}
