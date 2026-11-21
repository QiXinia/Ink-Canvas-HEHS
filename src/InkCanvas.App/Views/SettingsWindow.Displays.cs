using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class SettingsWindow
{
    private void BuildDisplaySettings()
    {
        S.Display ??= new();
        S.Display.EnabledDevices ??= new();
        var screens = ScreenService.All().OrderByDescending(screen => screen.IsPrimary).ThenBy(screen => screen.DeviceName).ToArray();
        var choices = new StackPanel { Spacing = 8 };
        var choiceHost = new UserControl { Content = choices };
        _content.Children.Add(ToggleRow("所有显示器均可书写", () => S.Display.AllDisplays, value =>
        {
            if (!value && S.Display.EnabledDevices.Count == 0)
                S.Display.EnabledDevices.AddRange(screens.Select(screen => screen.DeviceName));
            S.Display.AllDisplays = value;
            choiceHost.IsEnabled = !value;
        }, note: "关闭后可分别选择允许屏幕批注的显示器，支持多个触摸屏同时书写。"));
        choiceHost.IsEnabled = !S.Display.AllDisplays;
        foreach (var screen in screens)
        {
            string device = screen.DeviceName;
            string label = device.Replace(@"\\.\", "") + (screen.IsPrimary ? " · 主显示器" : "")
                + $" · {screen.Bounds.Width} × {screen.Bounds.Height} · {screen.DpiScale:P0}";
            choices.Children.Add(Require(ToggleRow(label, () => S.Display.AllDisplays || S.Display.EnabledDevices.Contains(device, StringComparer.OrdinalIgnoreCase), value =>
            {
                S.Display.EnabledDevices.RemoveAll(item => string.Equals(item, device, StringComparison.OrdinalIgnoreCase));
                if (value) S.Display.EnabledDevices.Add(device);
            }), () => S.Display.AllDisplays ? "请先关闭“所有显示器均可书写”，再单独选择显示器。" : null));
        }
        _content.Children.Add(choiceHost);
        _content.Children.Add(InfoRow("未启用的显示器可以直接操作下方应用。显示器选择会保存，连接状态变化后自动应用。不同屏幕上的触摸分别处理，同一屏幕仍遵循手势与多指书写设置。"));
    }
}
