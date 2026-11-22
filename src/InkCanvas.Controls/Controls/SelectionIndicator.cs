using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared selected-tool surface and Fluent selection indicator.</summary>
public sealed class SelectionIndicator : Grid
{
    private readonly Border _surface;
    private readonly Border _marker;

    public SelectionIndicator()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        _surface = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Background="{ThemeResource SubtleFillColorSecondaryBrush}"
                BorderBrush="{ThemeResource ControlStrongStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="4" />
            """);
        _marker = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                Width="18" Height="3" CornerRadius="1.5" Margin="4,0,4,2"
                Background="{ThemeResource AccentFillColorDefaultBrush}"
                HorizontalAlignment="Center" VerticalAlignment="Bottom" />
            """);
        Children.Add(_surface);
        Children.Add(_marker);
    }

    public new CornerRadius CornerRadius { get => _surface.CornerRadius; set => _surface.CornerRadius = value; }
    public bool IsSelected
    {
        get => Visibility == Visibility.Visible;
        set => Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UseLeadingMarker()
    {
        _marker.Width = 3;
        _marker.Height = 20;
        _marker.Margin = new Thickness(2, 4, 0, 4);
        _marker.HorizontalAlignment = HorizontalAlignment.Left;
        _marker.VerticalAlignment = VerticalAlignment.Center;
    }
}
