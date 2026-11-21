using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private async Task CalibrateTouchAsync()
    {
        var message = new TextBlock { Text = "请用常用的触控笔在下方区域轻点数次。", TextWrapping = TextWrapping.Wrap };
        var samples = new List<double>();
        var target = new Border { Height = 180, MinWidth = 280, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 103, 192)),
            Child = new TextBlock { Text = "触摸校准区域", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var dialog = new ContentDialog { Title = "触摸大小倍数校准", PrimaryButtonText = "应用校准", CloseButtonText = "取消",
            IsPrimaryButtonEnabled = false, XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Content = new StackPanel { Spacing = 12, Children = { message, target } } };
        target.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(target);
            if (point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch) return;
            var contact = point.Properties.ContactRect;
            double multiplier = AutomationPolicy.CalibrateTouch(contact.Width, contact.Height, S.Advanced.IsQuadIR);
            if (!double.IsFinite(multiplier)) { message.Text = "驱动未提供有效接触尺寸，请检查触摸驱动。"; return; }
            samples.Add(multiplier);
            message.Text = $"已采集 {samples.Count} 次，建议倍数为 {samples.Order().ElementAt(samples.Count / 2):0.00}";
            dialog.IsPrimaryButtonEnabled = true;
            args.Handled = true;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && samples.Count > 0)
        {
            S.Advanced.TouchMultiplier = samples.Order().ElementAt(samples.Count / 2);
            Commit();
        }
    }
}
