using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Viddl.Core.Settings;

namespace Viddl.App;

/// <summary>Simple message, confirmation and text-input dialogs.</summary>
public static class Dialogs
{
    public static async Task ShowAsync(XamlRoot root, string title, string message)
    {
        var d = new ContentDialog { XamlRoot = root, Title = title, Content = Wrap(message), CloseButtonText = "OK" };
        await d.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string yes = "Yes", string no = "No")
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = Wrap(message),
            PrimaryButtonText = yes, CloseButtonText = no, DefaultButton = ContentDialogButton.Primary,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> PromptAsync(XamlRoot root, string title, string label, string value)
    {
        var box = new TextBox { Header = label, Text = value, MinWidth = 380 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = box,
            PrimaryButtonText = "OK", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private static TextBlock Wrap(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}

/// <summary>Organize favorites: open, rename, delete and reorder.</summary>
public sealed class FavoritesDialog : ContentDialog
{
    private readonly List<SiteEntry> _favorites;
    private readonly ObservableCollection<string> _labels = new();
    private readonly ListView _list = new() { Height = 320, Width = 520, SelectionMode = ListViewSelectionMode.Single };

    public FavoritesDialog(XamlRoot root, List<SiteEntry> favorites)
    {
        _favorites = favorites;
        XamlRoot = root;
        Title = "Organize favorites";
        CloseButtonText = "Close";
        PrimaryButtonText = "Open";
        DefaultButton = ContentDialogButton.Primary;
        _list.ItemsSource = _labels;

        var buttons = new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(MakeButton("Rename…", async () => await RenameAsync()));
        buttons.Children.Add(MakeButton("Delete", Delete));
        buttons.Children.Add(MakeButton("Move up", () => MoveSelected(-1)));
        buttons.Children.Add(MakeButton("Move down", () => MoveSelected(+1)));
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(_list);
        Grid.SetColumn(buttons, 1);
        layout.Children.Add(buttons);
        Content = layout;

        PrimaryButtonClick += (_, e) =>
        {
            if (_list.SelectedIndex < 0) { e.Cancel = true; return; }
            UrlToOpen = _favorites[_list.SelectedIndex].Url;
        };
        Refill(0);
    }

    /// <summary>Set when the user chose "Open".</summary>
    public string? UrlToOpen { get; private set; }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 110 };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Refill(int select)
    {
        _labels.Clear();
        foreach (var f in _favorites) _labels.Add($"{f.DisplayTitle}\n{f.Url}");
        if (_favorites.Count > 0) _list.SelectedIndex = Math.Clamp(select, 0, _favorites.Count - 1);
    }

    private async Task RenameAsync()
    {
        int i = _list.SelectedIndex;
        if (i < 0) return;
        // Only one ContentDialog can be open at a time, so hide this one while asking.
        Hide();
        var name = await Dialogs.PromptAsync(XamlRoot, "Rename favorite", "Name", _favorites[i].DisplayTitle);
        if (!string.IsNullOrWhiteSpace(name)) _favorites[i].Title = name.Trim();
        Refill(i);
        _ = ShowAsync();
    }

    private void Delete()
    {
        int i = _list.SelectedIndex;
        if (i < 0) return;
        _favorites.RemoveAt(i);
        Refill(i);
    }

    private void MoveSelected(int delta)
    {
        int i = _list.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _favorites.Count) return;
        (_favorites[i], _favorites[j]) = (_favorites[j], _favorites[i]);
        Refill(j);
    }
}

/// <summary>Win32 calls for the "done" sound and taskbar flashing.</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Flashes the taskbar button until the window is focused (only if it isn't already).</summary>
    public static void FlashTaskbar(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;
        var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = 0x3 | 0xC, uCount = uint.MaxValue };
        FlashWindowEx(ref info);
    }

    public static void Beep(bool error) => MessageBeep(error ? 0x30u : 0x40u); // MB_ICONWARNING / MB_ICONASTERISK
}
