using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class SelectionTouchRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var settings = new GestureSettings { IsEnableMultiTouchMode = true, IsEnableTwoFingerRotationOnSelection = true };
        var panel = new BoardGesturePanel(settings, 1);
        var window = new Window { Title = "选区双指手势检查", Content = panel };
        try
        {
            WindowChrome.MakeStandard(window, 400, 520);
            WindowChrome.ShowWithoutActivation(window);
            await Task.Delay(180);
            var rotation = Descendants(panel).OfType<ToggleSwitch>().Single(toggle => AutomationProperties.GetName(toggle) == "双指旋转选区");
            checks.Add(new { check = "selection rotation remains editable during multi-finger writing", passed = rotation.IsEnabled });
            ((IToggleProvider)new ToggleSwitchAutomationPeer(rotation).GetPattern(PatternInterface.Toggle)).Toggle();
            checks.Add(new { check = "selection rotation updates independently of writing mode", passed = settings.IsEnableMultiTouchMode
                && !settings.IsEnableTwoFingerRotationOnSelection });
            await SelectionDragRegression.Run(checks, directory);
            bool compatible = SelectionRegression.Run(checks);
            checks.Add(new { check = "selection input and mixed object history remain compatible", passed = compatible });
        }
        catch (Exception exception) { checks.Add(new { check = "selection touch diagnostic exception", passed = false, error = exception.ToString() }); }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(directory, "selection-touch.json"), JsonSerializer.Serialize(checks,
                new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
