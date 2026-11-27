using InkCanvas.Core.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>A small ink swatch with a theme-aware outline, positioned within a tool icon.</summary>
public sealed class InkColorBadge : Grid
{
    private readonly SolidColorBrush _fill = new();
    private InkColor _color;

    public InkColorBadge()
    {
        Width = Height = 10;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        var outline = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                BorderBrush="{ThemeResource ControlStrongStrokeColorDefaultBrush}"
                BorderThickness="1" CornerRadius="5" />
            """);
        outline.Child = new Border { Background = _fill, CornerRadius = new CornerRadius(3), Margin = new Thickness(1) };
        Children.Add(outline);
    }

    /// <summary>Shows the RGB color at full opacity so highlighter opacity cannot hide the swatch.</summary>
    public InkColor Color
    {
        get => _color;
        set
        {
            _color = value.WithAlpha(255);
            _fill.Color = Windows.UI.Color.FromArgb(255, value.R, value.G, value.B);
        }
    }
}
