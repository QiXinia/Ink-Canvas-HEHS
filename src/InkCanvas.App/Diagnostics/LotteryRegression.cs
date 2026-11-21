using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;

namespace InkCanvas.App.Diagnostics;

internal static class LotteryRegression
{
    internal static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services) { RosterDirectory = Path.Combine(directory, "roster") };
        shell.Start(); shell.StartInClickThrough();
        bool cursorAvailable = NativeMethods.GetCursorPos(out var originalCursor);
        bool systemAnimations = Motion.AnimationsEnabled;
        try
        {
            shell.OpenLottery();
            await Task.Delay(220);
            var lottery = shell.OpenWindows.OfType<LotteryWindow>().Single();
            for (int attempt = 0; attempt < 100 && Field<TouchIconButton>(lottery, "_optionsButton").XamlRoot is null; attempt++)
                await Task.Delay(20);
            var controller = services.Lottery;
            controller.LoadNames(Enumerable.Range(1, 60).Select(index => $"同学{index}"));
            var grid = Descendants(lottery.Content).OfType<GridView>().Single();
            Check("lottery uses native WinUI title bar", lottery.Content is WindowFrame && lottery.ExtendsContentIntoTitleBar);
            Check("soft cooldown is the default with a ten-day half-life", controller.SamplingMode == LotterySamplingMode.SoftCooldown
                && controller.CooldownHalfLifeDays == 10 && controller.CooldownPenaltyStrength == 1);
            var optionsButton = Field<TouchIconButton>(lottery, "_optionsButton");
            Invoke(optionsButton); await Task.Delay(100);
            var options = (StackPanel)((Flyout)optionsButton.Flyout).Content;
            var modeBox = Descendants(options).OfType<ComboBox>().Single();
            var daysBox = Descendants(options).OfType<NumberBox>().Single(number => AutomationProperties.GetName(number) == "冷却半衰期（天）");
            daysBox.Value = 14;
            Check("popup changes and persists the cooldown period", controller.CooldownHalfLifeDays == 14 && services.Settings.Rand.CooldownHalfLifeDays == 14);
            modeBox.SelectedIndex = (int)LotterySamplingMode.Uniform;
            Check("uniform mode keeps period setting visible with an explanation", !daysBox.IsEnabled && ToolTipService.GetToolTip(daysBox) is string);
            modeBox.SelectedIndex = (int)LotterySamplingMode.SoftCooldown;
            daysBox.Value = 10;
            await BoardWindowRegression.SaveVisual(options, Path.Combine(directory, "cooldown-options.png"));
            optionsButton.Flyout.Hide();
            foreach (int theme in new[] { 1, 2 })
            {
                WindowChrome.ApplyTheme(lottery, theme);
                foreach (int count in new[] { 1, 2, 5, 10, 30 })
                {
                    SetAnimations(true);
                    lottery.ChangeCount(count);
                    Task roll = lottery.RollAsync();
                    await Task.Delay(100);
                    Check($"all preview cards roll together {theme} {count}", grid.Items.Count == Math.Min(count, 24)
                        && !Field<TouchIconButton>(lottery, "_plusButton").IsEnabled && controller.History.Count == (theme - 1) * 5 + Array.IndexOf(new[] { 1, 2, 5, 10, 30 }, count));
                    var before = grid.Items.Cast<LotteryWindow.ResultItem>().Select(item => item.Name).ToArray();
                    bool changed = false;
                    for (int sample = 0; sample < 4 && !changed; sample++)
                    {
                        await Task.Delay(100);
                        changed = !grid.Items.Cast<LotteryWindow.ResultItem>().Select(item => item.Name).SequenceEqual(before);
                    }
                    Check($"preview updates without consuming draw history {theme} {count}", changed);
                    await roll;
                    Check($"final cards retain every selected name {theme} {count}", grid.Items.Cast<LotteryWindow.ResultItem>().Select(item => item.Name).SequenceEqual(lottery.LastResult)
                        && lottery.LastResult.Count == count && lottery.LastResult.Distinct().Count() == count);
                    foreach (int width in count is 10 or 30 ? new[] { 600, 1100 } : new[] { 1100 })
                    {
                        Resize(lottery, width, width == 600 ? 540 : 720);
                        await Task.Delay(100);
                        Check($"result cards fit viewport {theme} {count} {width}", Descendants(grid).OfType<GridViewItem>().All(item => FitsHorizontally(item, grid)));
                        Check($"touch controls fit client {theme} {count} {width}", Descendants(lottery.Content).OfType<TouchIconButton>().Where(Visible)
                            .All(button => Fits(button, (FrameworkElement)lottery.Content) && button.ActualWidth >= 55 && button.ActualHeight >= 55));
                        if (count is 1 or 10 or 30) await BoardWindowRegression.SaveVisual((FrameworkElement)lottery.Content,
                            Path.Combine(directory, $"lottery-{theme}-{count}-{width}.png"));
                    }
                }
            }
            controller.LoadNames(Enumerable.Range(1, 5000).Select(index => $"同学{index}"));
            lottery.ChangeCount(5000); await lottery.RollAsync();
            Check("large batches virtualize result cards", lottery.LastResult.Count == 5000 && grid.Items.Count == 5000
                && Descendants(grid).OfType<GridViewItem>().Count() < 100);
            grid.ScrollIntoView(grid.Items[4999]); await Task.Delay(120);
            Check("large batch remains touch scrollable to the last name", grid.ContainerFromIndex(4999) is GridViewItem);
            controller.LoadNames(Enumerable.Range(1, 60).Select(index => $"同学{index}"));
            controller.NoRepeat = true; controller.Reset();
            lottery.ChangeCount(59); await lottery.RollAsync();
            lottery.ChangeCount(10);
            Check("last batch shows the actual remaining draw count", Field<TextBlock>(lottery, "_countLabel").Text == "1");
            await lottery.RollAsync();
            Check("last batch contains only remaining names", lottery.LastResult.Count == 1 && controller.RemainingCount == 0
                && !Field<TouchIconButton>(lottery, "_rollButton").IsEnabled);
            Invoke(Field<TouchIconButton>(lottery, "_resetRound"));
            Check("reset clears stale results and restores the round", lottery.LastResult.Count == 0 && controller.RemainingCount == 60 && grid.Items.Count == 0);
            controller.NoRepeat = false;
            controller.LoadNames(new[] { "欧阳同学（高一年级一班）", "Smith, John", "同名", "同名", "李四", "王五" });
            lottery.ChangeCount(6); await lottery.RollAsync();
            await Task.Delay(180);
            Check("long and repeated names retain separate result cards", lottery.LastResult.Count == 6
                && grid.Items.Cast<LotteryWindow.ResultItem>().Count(item => item.Name == "同名") == 2);
            Check("long names fit their cards", Descendants(grid).OfType<TextBlock>().All(text => !text.IsTextTrimmed));
            await BoardWindowRegression.SaveVisual((FrameworkElement)lottery.Content, Path.Combine(directory, "lottery-long-names.png"));
            Check("secondary actions combine icons and readable text", new[] { "_resetRound", "_copy", "_importButton", "_optionsButton", "_rollButton" }
                .Select(name => Field<TouchIconButton>(lottery, name)).All(button => Descendants(button).OfType<FontIcon>().Any()
                    && Descendants(button).OfType<TextBlock>().Any(text => !string.IsNullOrWhiteSpace(text.Text))));
            var rosterButton = Field<TouchIconButton>(lottery, "_importButton");
            var rosterBounds = WindowChrome.GetScreenBounds(lottery, rosterButton);
            SetCursorPos(rosterBounds.X + rosterBounds.Width / 2, rosterBounds.Y + rosterBounds.Height / 2);
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40);
            MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(250);
            if (!shell.OpenWindows.OfType<SettingsWindow>().Any()) Invoke(rosterButton);
            for (int attempt = 0; attempt < 100 && !shell.OpenWindows.OfType<SettingsWindow>()
                .Any(window => Descendants(window.Content).OfType<ClassroomRosterEditor>().Any()); attempt++) await Task.Delay(20);
            var settings = shell.OpenWindows.OfType<SettingsWindow>().Single();
            Check("roster action navigates to settings", Descendants(settings.Content).OfType<WindowWorkspace>().Single().SelectedPage == "抽奖与点名"
                && Descendants(settings.Content).OfType<ClassroomRosterEditor>().Count() == 1);
            var rosterBox = Descendants(settings.Content).OfType<ClassroomRosterEditor>().Single().Children.OfType<TextBox>().Single();
            var rosterRect = rosterBox.TransformToVisual((FrameworkElement)settings.Content).TransformBounds(
                new Windows.Foundation.Rect(0, 0, rosterBox.ActualWidth, rosterBox.ActualHeight));
            var settingsWorkspace = Descendants(settings.Content).OfType<WindowWorkspace>().Single();
            var settingsScroll = Field<ScrollViewer>(settingsWorkspace, "_scroll");
            await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, "roster-jump.png"));
            checks.Add(new { check = "roster jump scrolls the editor into view", passed = InScrollViewport(rosterBox, settingsScroll),
                rosterRect.Top, rosterRect.Bottom, rosterRect.Height, rootHeight = ((FrameworkElement)settings.Content).ActualHeight,
                settingsScroll.VerticalOffset, settingsScroll.ScrollableHeight,
                pending = Field<bool>(settings, "_revealRoster") });
            var caption = WindowChrome.GetScreenBounds(settings, ((WindowFrame)settings.Content).Header);
            // Grant this diagnostic foreground input in the same way as a user click.
            NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(settings));
            SetCursorPos(caption.X + 180, caption.Y + 24); MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(30); MouseEvent(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(1050);
            var settingsHit = GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = caption.X + 30, Y = caption.Y + 24 }), 2);
            Check("roster settings remains non-topmost", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(settings), NativeMethods.WS_EX_TOPMOST));
            bool foregroundAvailable = NativeMethods.GetForegroundWindow() == WindowChrome.GetHwnd(settings);
            checks.Add(new { check = "foreground roster settings receives native input", available = foregroundAvailable,
                passed = foregroundAvailable ? (bool?)(settingsHit == WindowChrome.GetHwnd(settings)) : null,
                hit = settingsHit.ToInt64(), expected = WindowChrome.GetHwnd(settings).ToInt64(),
                foreground = NativeMethods.GetForegroundWindow().ToInt64(), sourceVisible = lottery.AppWindow.IsVisible,
                caption = new { caption.X, caption.Y, caption.Width, caption.Height }, overlay = shell.Overlay.Window.Handle.ToInt64() });
            var editor = Descendants(settings.Content).OfType<ClassroomRosterEditor>().Single();
            var settingDays = Descendants(settings.Content).OfType<NumberBox>().Single(number => AutomationProperties.GetName(number) == "冷却半衰期（天）");
            var settingStrength = Descendants(settings.Content).OfType<NumberBox>().Single(number => AutomationProperties.GetName(number) == "软冷却惩罚强度");
            settingDays.Value = 12; settingStrength.Value = 2;
            Check("settings updates live cooldown parameters", controller.CooldownHalfLifeDays == 12 && controller.CooldownPenaltyStrength == 2);
            Check("settings search includes the half-life", settings.SearchSettings("半衰期").Any(entry => entry.Title == "冷却半衰期（天）"));
            settingDays.Value = 10; settingStrength.Value = 1;
            editor.RosterText = "学号,姓名\n1,张三\n2,李四\n3,王五";
            settings.SelectCategory("外观选项"); settings.SelectCategory("抽奖与点名");
            ((FrameworkElement)settings.Content).UpdateLayout();
            await Task.Delay(100);
            Check("roster draft survives category navigation", ReferenceEquals(editor, Descendants(settings.Content).OfType<ClassroomRosterEditor>().Single())
                && editor.RosterText.Contains("张三"));
            Check("settings search locates roster editor", settings.SearchSettings("名单").Any(entry => entry.Title == "名单管理"));
            Check("settings roster saves and updates active lottery", await editor.SaveAsync() && controller.Names.SequenceEqual(new[] { "张三", "李四", "王五" })
                && lottery.LastResult.Count == 0 && File.ReadAllLines(Path.Combine(shell.RosterDirectory, "Names.txt")).Length == 3);
            await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, "settings-roster.png"));
            settings.Close();
            Check("closing roster settings returns to lottery", lottery.AppWindow.IsVisible);
            Check("returning from settings preserves icon button touch targets", rosterButton.IsEnabled);
            lottery.ChangeCount(3); await lottery.RollAsync();
            Check("saved roster is used by the next draw", lottery.LastResult.Order().SequenceEqual(new[] { "张三", "李四", "王五" }.Order()));
            lottery.ChangeCount(2);
            Task canceled = lottery.RollAsync();
            controller.LoadNames(new[] { "新名单甲", "新名单乙" });
            await canceled;
            Check("changing roster cancels old preview and history", lottery.LastResult.Count == 0 && controller.History.Count == 0 && grid.Items.Count == 0);
            SetAnimations(false);
            Task reducedMotion = lottery.RollAsync();
            Check("reduced motion presents final results without flashing previews", reducedMotion.IsCompletedSuccessfully && lottery.LastResult.Count == 2);
            SetAnimations(true);
            canceled = lottery.RollAsync(); lottery.Close(); await canceled;
            Check("closing lottery cancels preview safely", canceled.IsCompletedSuccessfully);
        }
        catch (Exception exception) { checks.Add(new { check = "lottery regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            SetAnimations(systemAnimations);
            if (cursorAvailable) SetCursorPos(originalCursor.X, originalCursor.Y);
            foreach (var window in shell.OpenWindows.Reverse().ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "lottery.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static void Resize(Window window, int width, int height)
    {
        var work = WindowChrome.GetWorkArea(window); double dpi = WindowChrome.GetDpiScale(window);
        WindowChrome.Place(window, work.X + 24, work.Y + 24, (int)(width * dpi), (int)(height * dpi));
    }
    private static bool Visible(FrameworkElement element) => element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0;
    private static bool Fits(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
    }
    private static bool FitsHorizontally(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -1 && bounds.Right <= root.ActualWidth + 1;
    }
    private static bool InScrollViewport(FrameworkElement element, ScrollViewer scroll)
    {
        // ScrollViewer applies its live offset in Composition. Compare content coordinates to
        // the logical viewport instead of using the outer XAML transform as a screen position.
        var bounds = element.TransformToVisual((UIElement)scroll.Content).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Top >= scroll.VerticalOffset - 1 && bounds.Bottom <= scroll.VerticalOffset + scroll.ViewportHeight + 1;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
    private static void SetAnimations(bool enabled)
    {
        typeof(Motion).GetField("_cachedEnabled", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, enabled);
        typeof(Motion).GetField("_cachedAt", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, DateTime.UtcNow);
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
