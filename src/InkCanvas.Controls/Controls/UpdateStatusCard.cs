using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Controls.Controls;

public sealed class UpdateStatusCard : UserControl
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _cancel = new() { Content = "取消", MinHeight = 40, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
    public event Action CancelRequested;

    public UpdateStatusCard(int theme)
    {
        _status.Foreground = PopupBrushes.Foreground(theme);
        _details.Foreground = PopupBrushes.Foreground(theme);
        AutomationProperties.SetName(_status, "更新状态");
        AutomationProperties.SetName(_progress, "更新进度");
        AutomationProperties.SetName(_cancel, "取消更新");
        _cancel.Click += (_, _) => CancelRequested?.Invoke();
        Content = new Border
        {
            Background = PopupBrushes.Surface(theme), BorderBrush = PopupBrushes.ToolPanelBorder(theme),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12),
            Child = new StackPanel { Spacing = 8, Children = { _status, _progress, _details, _cancel } },
        };
    }

    public void Update(string status, string details, double? fraction, bool showProgress, bool canCancel)
    {
        _status.Text = status;
        _details.Text = details ?? "";
        _details.Visibility = string.IsNullOrWhiteSpace(details) ? Visibility.Collapsed : Visibility.Visible;
        _progress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = showProgress && !fraction.HasValue;
        _progress.Value = Math.Clamp(fraction.GetValueOrDefault(), 0, 1) * 100;
        _cancel.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
    }
}
