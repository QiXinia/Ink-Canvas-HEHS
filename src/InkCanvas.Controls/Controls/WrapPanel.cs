using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace InkCanvas.Controls.Controls;

/// <summary>Arranges controls in horizontal rows that wrap to the available width.</summary>
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = Math.Max(0, availableSize.Width);
        double rowWidth = 0, rowHeight = 0, measuredWidth = 0, height = 0;
        foreach (var child in Children.Where(child => child.Visibility != Visibility.Collapsed))
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + Spacing + size.Width > width)
            {
                measuredWidth = Math.Max(measuredWidth, rowWidth);
                height += rowHeight + Spacing;
                rowWidth = rowHeight = 0;
            }
            if (rowWidth > 0) rowWidth += Spacing;
            rowWidth += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(Math.Max(measuredWidth, rowWidth), height + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children.Where(child => child.Visibility != Visibility.Collapsed))
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += rowHeight + Spacing;
                x = rowHeight = 0;
            }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
            x += size.Width + Spacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return finalSize;
    }
}
