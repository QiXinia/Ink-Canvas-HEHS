using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Settings;
using InkCanvas.Ink;
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
            var canvasGestures = Descendants(panel).OfType<ToggleSwitch>().Skip(1).Take(3).ToArray();
            checks.Add(new { check = "board canvas gestures reject input during multi-finger writing",
                passed = canvasGestures.All(RejectsToggle) });
            foreach (int theme in new[] { 1, 2 })
            foreach (bool documentMode in new[] { false, true })
                await CheckFloatingGestureAvailability(checks, theme, documentMode);
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

    private static async Task CheckFloatingGestureAvailability(List<object> checks, int theme, bool documentMode)
    {
        var settings = new AppSettings();
        var gestures = documentMode ? new GestureSettings() : settings.Gesture;
        gestures.IsEnableTwoFingerRotation = true;
        using var controller = new InkController();
        var bar = new FloatingBar { Settings = settings, Controller = controller, RequestedTheme = theme == 2 ? ElementTheme.Dark : ElementTheme.Light,
            GestureSettingsOverride = documentMode ? gestures : null, SupportsViewRotation = !documentMode };
        bar.Build();
        var content = new StackPanel();
        content.Children.Add(bar);
        var options = bar.CreateGesturePanelContent();
        content.Children.Add(options);
        var window = new Window { Title = "手势禁用状态检查", Content = content };
        try
        {
            WindowChrome.MakeStandard(window, 1100, 400);
            WindowChrome.ApplyTheme(window, theme);
            WindowChrome.ShowWithoutActivation(window);
            await Task.Delay(80);
            var toggles = Descendants(options).OfType<ToggleSwitch>().ToArray();
            int edits = 0;
            bar.SettingsEdited += () => edits++;
            Check("canvas gesture switches reject input while disabled", toggles.Skip(1).All(RejectsToggle) && edits == 0);

            foreach (var toggle in toggles.Skip(1)) toggle.IsOn = false;
            Check("disabled gesture events preserve settings", gestures.IsEnableTwoFingerTranslate
                && gestures.IsEnableTwoFingerZoom && gestures.IsEnableTwoFingerRotation && edits == 0);
            bar.RefreshSettingsVisuals();
            Check("settings refresh preserves disabled values", toggles.Skip(1).All(toggle => toggle.IsOn && !toggle.IsEnabled) && edits == 0);

            Toggle(toggles[0]);
            Check("single-finger mode restores available gestures", !gestures.IsEnableMultiTouchMode && !controller.MultiTouchEnabled
                && toggles[1].IsEnabled && toggles[2].IsEnabled && toggles[3].IsEnabled == !documentMode);
            Toggle(toggles[1]); Toggle(toggles[2]);
            Check("available switches update and save settings", !gestures.IsEnableTwoFingerTranslate && !gestures.IsEnableTwoFingerZoom && edits == 3);
            if (documentMode)
                Check("document rotation remains disabled outside multi-finger mode", RejectsToggle(toggles[3]) && gestures.IsEnableTwoFingerRotation);
            else
            {
                Toggle(toggles[3]);
                Check("desktop rotation remains editable", !gestures.IsEnableTwoFingerRotation && edits == 4);
            }

            Toggle(toggles[0]);
            bar.RefreshSettingsVisuals();
            Check("returning to multi-finger mode disables without losing preferences", toggles.Skip(1).All(toggle => !toggle.IsEnabled)
                && !toggles[1].IsOn && !toggles[2].IsOn && gestures.IsEnableTwoFingerRotation == documentMode);
        }
        finally { window.Close(); }

        void Check(string name, bool passed) => checks.Add(new { check = name, theme, documentMode, passed });
    }

    private static void Toggle(ToggleSwitch toggle) =>
        ((IToggleProvider)new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();

    private static bool RejectsToggle(ToggleSwitch toggle)
    {
        bool previous = toggle.IsOn;
        try { Toggle(toggle); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80040200)) { } // UIA_E_ELEMENTNOTENABLED
        return !toggle.IsEnabled && toggle.IsOn == previous;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
