using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Viddl.App;

/// <summary>
/// A thin bar that resizes a neighbouring grid column or row by dragging.
/// Set <see cref="Target"/> to the column/row whose size changes and <see cref="Invert"/> when that
/// column/row lies before the splitter (dragging towards it makes it smaller).
/// </summary>
public sealed class Splitter : Grid
{
    private double _startPointer;
    private double _startSize;
    private bool _dragging;

    public Splitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => { _dragging = false; ReleasePointerCapture(e.Pointer); DragCompleted?.Invoke(); };
        PointerCaptureLost += (_, _) => _dragging = false;
        Loaded += (_, _) => ProtectedCursor = InputSystemCursor.Create(Horizontal ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
    }

    /// <summary>True for a splitter between rows (drag up/down).</summary>
    public bool Horizontal { get; set; }

    public bool Invert { get; set; }

    public double Minimum { get; set; } = 120;

    public double Maximum { get; set; } = 2000;

    public ColumnDefinition? TargetColumn { get; set; }

    public RowDefinition? TargetRow { get; set; }

    public event Action? DragCompleted;

    private double Position(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(XamlRoot.Content).Position;
        return Horizontal ? p.Y : p.X;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = CapturePointer(e.Pointer);
        _startPointer = Position(e);
        _startSize = Horizontal ? TargetRow?.ActualHeight ?? 0 : TargetColumn?.ActualWidth ?? 0;
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var delta = Position(e) - _startPointer;
        var size = Math.Clamp(_startSize + (Invert ? -delta : delta), Minimum, Maximum);
        if (Horizontal && TargetRow != null) TargetRow.Height = new GridLength(size);
        else if (TargetColumn != null) TargetColumn.Width = new GridLength(size);
        e.Handled = true;
    }
}
