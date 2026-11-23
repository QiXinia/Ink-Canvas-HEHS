using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>Pointer adjustment that works while its floating window remains inactive.</summary>
public sealed class PointerSlider : Slider
{
    private uint? _pointer;
    private Thumb _thumb;
    public bool IsAdjusting => _pointer.HasValue;
    public event Action AdjustmentCompleted;

    public PointerSlider()
    {
        AllowFocusOnInteraction = false;
        AddHandler(PointerMovedEvent, new PointerEventHandler(Moved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(Released), true);
        PointerCanceled += Canceled;
        PointerCaptureLost += (_, args) => { if (ReferenceEquals(args.OriginalSource, this)) Canceled(this, args); };
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(this) == 0 || VisualTreeHelper.GetChild(this, 0) is not Grid root) return;
        _thumb = FindThumb(root);
        // The native Thumb must not start a second drag or request focus after we capture.
        // Handle the press on a transparent surface before it bubbles to Slider's handlers.
        var input = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Grid.SetColumnSpan(input, Math.Max(1, root.ColumnDefinitions.Count));
        Grid.SetRowSpan(input, Math.Max(1, root.RowDefinitions.Count));
        input.PointerPressed += Pressed;
        root.Children.Add(input);
    }

    private void Pressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (!IsEnabled || _pointer.HasValue
            || point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
        if (!CapturePointer(args.Pointer)) return;
        _pointer = args.Pointer.PointerId;
        Adjust(point.Position.X);
        args.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        Adjust(args.GetCurrentPoint(this).Position.X);
        args.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        Adjust(args.GetCurrentPoint(this).Position.X);
        _pointer = null;
        ReleasePointerCapture(args.Pointer);
        AdjustmentCompleted?.Invoke();
        args.Handled = true;
    }

    private void Canceled(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        _pointer = null;
        AdjustmentCompleted?.Invoke();
    }

    private void Adjust(double x)
    {
        double halfThumb = _thumb?.ActualWidth / 2 ?? 10;
        double travel = ActualWidth - 2 * halfThumb;
        if (travel <= 0) return;
        double fraction = Math.Clamp((x - halfThumb) / travel, 0, 1);
        if (FlowDirection == FlowDirection.RightToLeft) fraction = 1 - fraction;
        double value = Minimum + fraction * (Maximum - Minimum);
        if (StepFrequency > 0) value = Minimum + Math.Round((value - Minimum) / StepFrequency) * StepFrequency;
        Value = Math.Clamp(value, Minimum, Maximum);
    }

    private static Thumb FindThumb(DependencyObject parent)
    {
        if (parent is Thumb thumb) return thumb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindThumb(VisualTreeHelper.GetChild(parent, i));
            if (found is not null) return found;
        }
        return null;
    }
}
