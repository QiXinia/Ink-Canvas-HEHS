using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Core.Theming;
using InkCanvas.Host.Contracts;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class CustomColorsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.DefaultDirectory = Path.Combine(directory, "archives");
        services.Settings.Archive.SaveOnExit = false;
        using var shell = new ShellController(services);
        var checks = new List<object>();
        try
        {
            shell.Start(); shell.StartInClickThrough();
            foreach (int theme in new[] { 1, 2 })
            {
                services.Settings.Appearance.Theme = theme;
                services.Settings.Canvas.ColorPalette = ColorPaletteKind.Light;
                services.Settings.Canvas.CustomColors.Clear();
                services.Settings.Canvas.EnableCustomColorPalette = true;
                services.Settings.Canvas.InkAlpha = 140;
                services.ApplySettings(); services.SaveSettings();
                shell.OpenPenPalette(false); await Task.Delay(120);
                var window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                var palette = Descendants(window.Content).OfType<PenPalette>().Single();
                var custom = Descendants(palette).OfType<InkColorPalette>().Single();
                foreach (string caption in new[] { "亮系", "暗系", "自定义" })
                {
                    CheckPalette(custom, 9, caption, "pen " + theme);
                    if (caption != "自定义") Invoke(Button(custom, "切换亮系/暗系/自定义墨迹颜色"));
                }

                Invoke(Button(custom, "添加自定义颜色"));
                var editor = await Editor();
                Check("quick editor accepts activation and preserves its parent " + theme,
                    !NativeMethods.HasExStyle(WindowChrome.GetHwnd(editor), NativeMethods.WS_EX_NOACTIVATE)
                    && shell.OpenWindows.Contains(window) && palette.IsEditingColor);
                Check("quick editor excludes canvas shortcuts " + theme, !shell.IsCanvasShortcutContext(WindowChrome.GetHwnd(editor)));
                Check("quick editor uses a floating panel with direct editing " + theme,
                    editor.Content is Border && editor.SystemBackdrop is MicaBackdrop
                    && !Descendants(editor.Content).OfType<Button>().Any(button => button.Content?.ToString() is "保存颜色" or "取消"));
                var editorBounds = WindowChrome.GetScreenBounds(editor, (FrameworkElement)editor.Content);
                var parentBounds = WindowChrome.GetScreenBounds(window, palette.CardSurface);
                var workArea = WindowChrome.GetWorkArea(editor);
                Check("quick editor fits beside the brush panel " + theme,
                    (editorBounds.X >= parentBounds.X + parentBounds.Width || editorBounds.X + editorBounds.Width <= parentBounds.X)
                    && editorBounds.X >= workArea.X && editorBounds.Y >= workArea.Y
                    && editorBounds.X + editorBounds.Width <= workArea.X + workArea.Width
                    && editorBounds.Y + editorBounds.Height <= workArea.Y + workArea.Height);
                var picker = Descendants(editor.Content).OfType<ColorPicker>().Single();
                Check("native quick editor exposes RGB and HEX " + theme,
                    picker.IsColorChannelTextInputVisible && picker.IsHexInputVisible && !picker.IsAlphaEnabled);
                picker.Color = Windows.UI.Color.FromArgb(255, 255, 238, 170);
                await Wait(() => services.Settings.Canvas.CustomColors.SequenceEqual(new[] { 0xffffeeaau }));
                var blue = Descendants(picker).OfType<TextBox>().Single(box => box.Text == "170");
                blue.Focus(FocusState.Programmatic); blue.Text = "169";
                Button(editor.Content, "关闭面板").Focus(FocusState.Programmatic);
                await Wait(() => services.Settings.Canvas.CustomColors[0] == 0xffffeea9);
                Check("RGB edits update and persist the same slot live " + theme,
                    services.Store.Load().Canvas.CustomColors.Count == 1 && services.OverlayInk.CurrentAttributes.Color.PackedArgb == 0xffffeea9);
                blue.Focus(FocusState.Programmatic); blue.Text = "170";
                Button(editor.Content, "关闭面板").Focus(FocusState.Programmatic);
                await Wait(() => services.Settings.Canvas.CustomColors[0] == 0xffffeeaa);
                await BoardWindowRegression.SaveVisual((FrameworkElement)editor.Content, Path.Combine(directory, "editor-" + theme + ".png"));
                bool? dismissed = await TryPointerPress(window, palette.Header, 30, () => editor.Completion.IsCompleted);
                checks.Add(new { check = "clicking outside closes the quick editor " + theme, passed = dismissed, available = dismissed.HasValue });
                if (dismissed != true) editor.Close();
                await Wait(() => !palette.IsEditingColor);
                Check("adding applies and saves without closing brush options " + theme,
                    services.Store.Load().Canvas.PenColor == 0xffffeeaa && shell.OpenWindows.Contains(window));
                palette.Header.IsPinned = true;
                var swatch = Descendants(custom).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("自定义颜色 1 ·"));
                var check = Descendants(swatch).OfType<FontIcon>().Single();
                var ink = ((SolidColorBrush)check.Foreground).Color;
                Check("custom colour selection keeps contrasting check mark " + theme,
                    ColorContrast.Ratio(InkColor.Opaque(255, 238, 170), InkColor.Opaque(ink.R, ink.G, ink.B)) >= 4.5);

                Invoke(Button(custom, "编辑自定义颜色")); editor = await Editor();
                picker = Descendants(editor.Content).OfType<ColorPicker>().Single();
                var hex = Descendants(picker).OfType<TextBox>().Single(box => box.Name == "HexTextBox");
                hex.Focus(FocusState.Programmatic); hex.Text = "#2468AC";
                Button(editor.Content, "关闭面板").Focus(FocusState.Programmatic);
                await Wait(() => services.Settings.Canvas.CustomColors[0] == 0xff2468ac);
                Check("HEX edits replace and save the active colour live " + theme,
                    services.Store.Load().Canvas.CustomColors.SequenceEqual(new[] { 0xff2468acu })
                    && services.OverlayInk.CurrentAttributes.Color.PackedArgb == 0xff2468ac);
                editor.Close(); await Wait(() => !palette.IsEditingColor);
                Invoke(Button(custom, "添加自定义颜色")); editor = await Editor();
                editor.Close(); await Wait(() => !palette.IsEditingColor);
                Check("closing untouched empty slot preserves colours " + theme, services.Store.Load().Canvas.CustomColors.Count == 1);

                Invoke(Button(custom, "添加自定义颜色")); editor = await Editor();
                Descendants(editor.Content).OfType<ColorPicker>().Single().Color = Windows.UI.Color.FromArgb(255, 80, 200, 120);
                editor.Close(); await Wait(() => !palette.IsEditingColor);
                Check("closing flushes the final colour change " + theme, services.Store.Load().Canvas.CustomColors.Last() == 0xff50c878);
                var second = Descendants(custom).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("自定义颜色 2 ·"));
                var menu = (MenuFlyout)second.ContextFlyout;
                menu.ShowAt(second); await Task.Delay(60);
                var remove = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "删除颜色");
                ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(remove).GetPattern(PatternInterface.Invoke)).Invoke();
                menu.Hide(); await Task.Delay(60);
                Check("right click deletion persists " + theme, services.Store.Load().Canvas.CustomColors.SequenceEqual(new[] { 0xff2468acu }));

                var target = Descendants(custom).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("自定义颜色 1 ·"));
                var beforeHold = services.OverlayInk.CurrentAttributes.Color;
                bool? nativeHold = await TryPointerPress(window, target, 700,
                    () => OpenPopupButtons(custom).Any(button => AutomationProperties.GetName(button) == "删除自定义颜色"));
                checks.Add(new { check = "native long press opens delete without selecting colour " + theme,
                    passed = nativeHold.HasValue ? nativeHold.Value && beforeHold == services.OverlayInk.CurrentAttributes.Color : (bool?)null,
                    available = nativeHold.HasValue });
                if (nativeHold != true)
                    custom.GetType().GetMethod("ShowDeleteFlyout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Invoke(custom, new object[] { target, 0 });
                await Task.Delay(80);
                var deleteButton = OpenPopupButtons(custom).Single(button => AutomationProperties.GetName(button) == "删除自定义颜色");
                var deleteBounds = WindowChrome.GetScreenBounds(window, deleteButton);
                var targetBounds = WindowChrome.GetScreenBounds(window, target);
                Check("delete action floats above its colour " + theme, deleteBounds.Y + deleteBounds.Height <= targetBounds.Y + 1);
                await BoardWindowRegression.SaveVisual(deleteButton, Path.Combine(directory, "delete-" + theme + ".png"));
                Invoke(deleteButton); await Task.Delay(60);
                Check("floating deletion saves and restores nine empty slots " + theme,
                    services.Store.Load().Canvas.CustomColors.Count == 0
                    && Descendants(custom).OfType<Button>().Count(button => AutomationProperties.GetName(button) == "添加自定义颜色") == 9);

                services.Settings.Canvas.CustomColors.Add(0xff2468ac);
                for (int index = 0; index < 20; index++) services.Settings.Canvas.CustomColors.Add(0xff000000u + (uint)(index * 0x00070311));
                services.ApplySettings(); services.SaveSettings(); await Task.Delay(80);
                CheckPalette(custom, 9, "自定义", "legacy overflow " + theme);
                Check("legacy overflow stays in saved configuration " + theme, services.Store.Load().Canvas.CustomColors.Count == 21);
                foreach (double scale in new[] { 50d, 100d, 150d, 200d })
                {
                    window.ApplyScale(scale); await Task.Delay(60);
                    var bounds = WindowChrome.GetScreenBounds(window, palette.CardSurface);
                    var work = WindowChrome.GetWorkArea(window);
                    Check($"palette fits work area {theme}-{scale}", bounds.X >= work.X - 1 && bounds.Y >= work.Y - 1
                        && bounds.X + bounds.Width <= work.X + work.Width + 1 && bounds.Y + bounds.Height <= work.Y + work.Height + 1);
                }
                window.ApplyScale(100); await Task.Delay(60);
                await BoardWindowRegression.SaveVisual(palette, Path.Combine(directory, "palette-" + theme + ".png"));
                services.Settings.Canvas.EnableCustomColorPalette = false;
                services.ApplySettings(); services.SaveSettings();
                Check("disabling custom colours retains saved slots " + theme,
                    services.Store.Load().Canvas.CustomColors.Count == 21 && Descendants(custom).OfType<Button>()
                        .Single(button => AutomationProperties.GetName(button) == "编辑自定义颜色").Visibility == Visibility.Collapsed);
                services.Settings.Canvas.EnableCustomColorPalette = true;
                services.Settings.Canvas.CustomColors.RemoveRange(1, 20);
                services.ApplySettings(); services.SaveSettings(); window.Close();

                shell.OpenPenPalette(true); await Task.Delay(100);
                window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                palette = Descendants(window.Content).OfType<PenPalette>().Single();
                custom = Descendants(palette).OfType<InkColorPalette>().Single();
                foreach (string caption in new[] { "自定义", "亮系", "暗系" })
                {
                    CheckPalette(custom, 7, caption, "highlighter " + theme);
                    Invoke(Button(custom, "切换亮系/暗系/自定义墨迹颜色"));
                }
                var alpha = Descendants(palette).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "荧光笔不透明度");
                Check("opening opacity preserves exact legacy alpha " + theme, alpha.Minimum == 0 && alpha.Maximum == 100
                    && alpha.Value == 55 && services.Store.Load().Canvas.InkAlpha == 140);
                foreach (int percent in new[] { 0, 50, 100, 55 })
                {
                    alpha.Value = percent;
                    int expected = OriginalMenuCatalog.ToStoredHighlighterAlpha(percent);
                    Check($"highlighter opacity applies and persists {theme}-{percent}",
                        services.Store.Load().Canvas.InkAlpha == expected && services.OverlayInk.CurrentAttributes.HighlighterAlpha == expected
                        && Descendants(palette).OfType<TextBlock>().Any(text => text.Text == percent + "%"));
                }
                await BoardWindowRegression.SaveVisual(palette, Path.Combine(directory, "highlighter-" + theme + ".png"));
                Invoke(Button(custom, "添加自定义颜色")); editor = await Editor();
                uint highlighterColor = 0xff50c878u - (uint)theme;
                Descendants(editor.Content).OfType<ColorPicker>().Single().Color = Windows.UI.Color.FromArgb(255, 80, 200, (byte)(120 - theme));
                await Wait(() => services.Settings.Canvas.CustomColors.Count == 2);
                Check("highlighter edits live and retains opacity " + theme, services.Store.Load().Canvas.HighlighterColor == highlighterColor
                    && services.OverlayInk.CurrentAttributes.HighlighterAlpha == services.Settings.Canvas.InkAlpha);
                editor.Close(); await Wait(() => !palette.IsEditingColor); window.Close();

                shell.ToggleBoard(); await Task.Delay(100);
                var board = shell.OpenWindows.OfType<BoardWindow>().Single();
                board.ShowPenPalette(); await Task.Delay(100);
                var boardPalette = board.ToolPanels.SelectMany(panel => Descendants(panel.Content)).OfType<BoardPenPalette>().Single();
                var penColors = Descendants(boardPalette).OfType<InkColorPalette>().Single(view => AutomationProperties.GetName(view) == "画笔色板");
                var highlighterColors = Descendants(boardPalette).OfType<InkColorPalette>().Single(view => AutomationProperties.GetName(view) == "荧光笔色板");
                CheckPalette(penColors, 9, "自定义", "board pen " + theme);
                CheckPalette(highlighterColors, 7, "自定义", "board highlighter " + theme);
                var boardAlpha = Descendants(boardPalette).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "荧光笔不透明度");
                foreach (int percent in new[] { 0, 50, 100, 55 })
                {
                    boardAlpha.Value = percent;
                    Check($"board opacity applies and persists {theme}-{percent}",
                        services.Store.Load().Canvas.InkAlpha == OriginalMenuCatalog.ToStoredHighlighterAlpha(percent)
                        && board.CurrentController.CurrentAttributes.HighlighterAlpha == OriginalMenuCatalog.ToStoredHighlighterAlpha(percent));
                }
                var firstPenColor = Descendants(penColors).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("自定义颜色 1 ·"));
                var editMenu = (MenuFlyout)firstPenColor.ContextFlyout;
                editMenu.ShowAt(firstPenColor); await Task.Delay(60);
                var editItem = editMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "编辑颜色");
                ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(editItem).GetPattern(PatternInterface.Invoke)).Invoke();
                editMenu.Hide(); editor = await Editor();
                Descendants(editor.Content).OfType<ColorPicker>().Single().Color = Windows.UI.Color.FromArgb(255, 36, 104, 173);
                await Wait(() => board.CurrentController.CurrentAttributes.Color.PackedArgb == 0xff2468ad);
                await Task.Delay(160);
                Check("board quick editor keeps its parent and saves live " + theme,
                    board.ToolPanels.Any(panel => panel.AppWindow.IsVisible) && services.Store.Load().Canvas.PenColor == 0xff2468ad);
                Descendants(editor.Content).OfType<ColorPicker>().Single().Color = Windows.UI.Color.FromArgb(255, 36, 104, 172);
                editor.Close(); await Wait(() => !boardPalette.IsEditingColor);
                await BoardWindowRegression.SaveVisual(boardPalette, Path.Combine(directory, "board-" + theme + ".png"));
                board.Close(); await Task.Delay(80);
            }

            var pdf = services.Modules.GetExport<IPdfWorkspaceService>();
            string pdfPath = Path.Combine(directory, "custom-colours.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics").GetMethod("CreateFixture",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { pdfPath, 2 });
            await shell.OpenPdfAsync(pdfPath); await Task.Delay(900);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            workspace.Window.Activate(); NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(workspace.Window));
            await Task.Delay(250);
            bool supplied = !ReferenceEquals(shell.OpenWindows.OfType<FloatingBarWindow>().Single().Bar.Controller, workspace.Controller);
            shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
            shell.OpenPenPalette(false);
            var pdfPalette = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
            pdfPalette.Activate();
            await Wait(() => Descendants(pdfPalette.Content).OfType<PenPalette>().Any());
            var pdfPenPalette = Descendants(pdfPalette.Content).OfType<PenPalette>().Single();
            pdfPenPalette.Header.IsPinned = true;
            await Task.Delay(80);
            Invoke(Button(pdfPalette.Content, "添加自定义颜色"));
            var pdfEditor = await Editor();
            Descendants(pdfEditor.Content).OfType<ColorPicker>().Single().Color = Windows.UI.Color.FromArgb(255, 233, 50, 100);
            await Wait(() => services.Settings.Canvas.CustomColors.LastOrDefault() == 0xffe93264);
            checks.Add(new { check = "PDF quick editor saves and updates document ink live", passed =
                workspace.Controller.CurrentAttributes.Color.PackedArgb == 0xffe93264 && services.Store.Load().Canvas.PenColor == 0xffe93264,
                foregroundContextSuppliedByTest = supplied, activeColor = workspace.Controller.CurrentAttributes.Color.PackedArgb,
                storedColor = services.Store.Load().Canvas.PenColor, paletteColor = pdfPenPalette.Controller.CurrentAttributes.Color.PackedArgb,
                paletteTargetsDocument = ReferenceEquals(pdfPenPalette.Controller, workspace.Controller) });
            pdfEditor.Close(); workspace.Window.Close();
        }
        catch (Exception error) { checks.Add(new { check = "custom colours exception", passed = false, error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "custom-colors.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }

        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        void CheckPalette(InkColorPalette colors, int slots, string caption, string name)
        {
            var grid = (Grid)Descendants(colors).OfType<ScrollViewer>().Single().Content;
            var content = ((Border)((Button)grid.Children[0]).Content).Child;
            string current = content is TextBlock text ? text.Text : ((StackPanel)content).Children.OfType<TextBlock>().Single().Text;
            Check("fixed slots and current group " + name + " " + caption, grid.Children.Count == slots + 1
                && grid.ColumnDefinitions.Count == (slots == 7 ? 4 : 5) && grid.RowDefinitions.Count == 2 && current == caption);
        }
        async Task<ColorEditorWindow> Editor()
        {
            await Wait(() => shell.OpenWindows.OfType<ColorEditorWindow>().Any());
            await Task.Delay(120);
            return shell.OpenWindows.OfType<ColorEditorWindow>().Single();
        }
    }

    private static async Task<bool?> TryPointerPress(Window window, FrameworkElement target, int milliseconds, Func<bool> result)
    {
        if (!NativeMethods.GetCursorPos(out var original)) return null;
        var bounds = WindowChrome.GetScreenBounds(window, target);
        int x = bounds.X + bounds.Width / 2, y = bounds.Y + bounds.Height / 2;
        bool down = false;
        try
        {
            if (!SetCursorPos(x, y) || GetAncestor(WindowFromPoint(new() { X = x, Y = y }), 2) != WindowChrome.GetHwnd(window)) return null;
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
            down = true;
            await Task.Delay(milliseconds);
            MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            down = false;
            await Task.Delay(180);
            return result();
        }
        finally
        {
            if (down) MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(original.X, original.Y);
        }
    }
    private static IEnumerable<Button> OpenPopupButtons(FrameworkElement root) => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
        .SelectMany(popup => Descendants(popup.Child)).OfType<Button>();
    private static async Task Wait(Func<bool> ready)
    {
        for (int count = 0; count < 100; count++) { if (ready()) return; await Task.Delay(30); }
        throw new TimeoutException("Custom colour interaction did not settle.");
    }
    private static Button Button(DependencyObject root, string name) => Descendants(root).OfType<Button>().First(button => AutomationProperties.GetName(button) == name);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
