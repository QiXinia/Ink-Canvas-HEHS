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
                services.ApplySettings(); services.SaveSettings();
                shell.OpenPenPalette(false); await Task.Delay(120);
                var window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                var palette = Descendants(window.Content).OfType<PenPalette>().Single();
                Invoke(Button(palette, "切换亮系/暗系/自定义墨迹颜色"));
                Check("light cycles to dark " + theme, services.Store.Load().Canvas.ColorPalette == ColorPaletteKind.Dark);
                Invoke(Button(palette, "切换亮系/暗系/自定义墨迹颜色"));
                Check("dark cycles to the persistent custom palette " + theme, services.Store.Load().Canvas.ColorPalette == ColorPaletteKind.Custom);
                Invoke(Button(palette, "添加自定义颜色"));
                var editor = await Editor();
                Check("colour editor accepts activation and keeps transient palette alive " + theme,
                    !NativeMethods.HasExStyle(WindowChrome.GetHwnd(editor), NativeMethods.WS_EX_NOACTIVATE)
                    && shell.OpenWindows.Contains(window));
                Check("colour editor keeps canvas shortcuts out of text inputs " + theme,
                    !shell.IsCanvasShortcutContext(WindowChrome.GetHwnd(editor)));
                foreach (var size in new[] { (340, 420), (640, 720), (400, 650) })
                {
                    editor.AppWindow.Resize(new(size.Item1, size.Item2)); await Task.Delay(50);
                    var save = Button(editor.Content, "保存自定义颜色");
                    var root = (FrameworkElement)editor.Content;
                    var bounds = save.TransformToVisual(root).TransformBounds(new(0, 0, save.ActualWidth, save.ActualHeight));
                    Check($"colour editor controls fit resized window {theme}-{size.Item1}", bounds.X >= 0 && bounds.Y >= 0
                        && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1);
                }
                var picker = Descendants(editor.Content).OfType<ColorPicker>().Single();
                Check("native editor exposes RGB and HEX inputs " + theme,
                    picker.IsColorChannelTextInputVisible && picker.IsHexInputVisible && !picker.IsAlphaEnabled);
                picker.Color = Windows.UI.Color.FromArgb(255, 255, 238, 170);
                await Task.Delay(80);
                var blue = Descendants(picker).OfType<TextBox>().Single(box => box.Text == "170");
                blue.Focus(FocusState.Programmatic);
                blue.Text = "169";
                Button(editor.Content, "保存自定义颜色").Focus(FocusState.Programmatic);
                Check("RGB channel input changes the native colour " + theme, picker.Color.B == 169);
                blue.Focus(FocusState.Programmatic);
                blue.Text = "170";
                Button(editor.Content, "保存自定义颜色").Focus(FocusState.Programmatic);
                await BoardWindowRegression.SaveVisual((FrameworkElement)editor.Content, Path.Combine(directory, "editor-" + theme + ".png"));
                Invoke(Button(editor.Content, "保存自定义颜色"));
                await Wait(() => !shell.OpenWindows.Contains(window));
                var stored = services.Store.Load().Canvas;
                Check("adding a colour saves and uses it " + theme, stored.CustomColors.SequenceEqual(new[] { 0xffffeeaau })
                    && stored.PenColor == 0xffffeeaa && services.OverlayInk.CurrentAttributes.Color.PackedArgb == 0xffffeeaa);

                shell.OpenPenPalette(false); await Task.Delay(80);
                window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                palette = Descendants(window.Content).OfType<PenPalette>().Single();
                palette.Header.IsPinned = true;
                var custom = Descendants(palette).OfType<InkColorPalette>().Single();
                var swatch = Descendants(custom).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("自定义颜色 1 ·"));
                var check = Descendants(swatch).OfType<FontIcon>().Single();
                var ink = ((SolidColorBrush)check.Foreground).Color;
                Check("selection mark contrasts with a light custom colour " + theme,
                    ColorContrast.Ratio(InkColor.Opaque(255, 238, 170), InkColor.Opaque(ink.R, ink.G, ink.B)) >= 4.5);
                Invoke(Button(custom, "编辑自定义颜色"));
                editor = await Editor();
                picker = Descendants(editor.Content).OfType<ColorPicker>().Single();
                var hex = Descendants(picker).OfType<TextBox>().Single(box => box.Name == "HexTextBox");
                hex.Focus(FocusState.Programmatic);
                hex.Text = "#2468AC";
                Button(editor.Content, "保存自定义颜色").Focus(FocusState.Programmatic);
                Check("HEX input changes the native colour " + theme, picker.Color.R == 36 && picker.Color.G == 104 && picker.Color.B == 172);
                Invoke(Button(editor.Content, "保存自定义颜色"));
                await Wait(() => services.Settings.Canvas.CustomColors[0] == 0xff2468ac);
                Check("editing replaces the colour and updates the active brush " + theme,
                    services.Store.Load().Canvas.CustomColors.SequenceEqual(new[] { 0xff2468acu })
                    && services.OverlayInk.CurrentAttributes.Color.PackedArgb == 0xff2468ac);
                Invoke(Button(custom, "添加自定义颜色"));
                editor = await Editor();
                Invoke(Descendants(editor.Content).OfType<Button>().Single(button => button.Content?.ToString() == "取消"));
                await Wait(() => !palette.IsEditingColor);
                Check("cancel leaves saved colours and brush intact " + theme,
                    services.Store.Load().Canvas.CustomColors.SequenceEqual(new[] { 0xff2468acu })
                    && services.OverlayInk.CurrentAttributes.Color.PackedArgb == 0xff2468ac);

                Invoke(Button(custom, "添加自定义颜色"));
                editor = await Editor();
                Descendants(editor.Content).OfType<ColorPicker>().Single().Color = Windows.UI.Color.FromArgb(255, 80, 200, 120);
                Invoke(Button(editor.Content, "保存自定义颜色"));
                await Wait(() => services.Settings.Canvas.CustomColors.Count == 2);
                var second = Descendants(custom).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("自定义颜色 2 ·"));
                var menu = (MenuFlyout)second.ContextFlyout;
                menu.ShowAt(second); await Task.Delay(60);
                var remove = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "删除颜色");
                ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(remove).GetPattern(PatternInterface.Invoke)).Invoke();
                menu.Hide();
                Check("context menu deletes a colour and saves the palette " + theme,
                    services.Store.Load().Canvas.CustomColors.SequenceEqual(new[] { 0xff2468acu }));

                for (int index = 0; index < 20; index++) services.Settings.Canvas.CustomColors.Add(0xff000000u + (uint)(index * 0x00070311));
                services.ApplySettings(); services.SaveSettings();
                await Task.Delay(100);
                var scroll = Descendants(custom).OfType<ScrollViewer>().Single();
                var grid = (Grid)scroll.Content;
                Check("large palette scrolls within its viewport " + theme,
                    scroll.ExtentHeight > scroll.ViewportHeight && scroll.ViewportHeight <= 133 && grid.ActualWidth <= scroll.ViewportWidth + 1);
                foreach (double scale in new[] { 50d, 100d, 150d, 200d })
                {
                    window.ApplyScale(scale); await Task.Delay(60);
                    var bounds = WindowChrome.GetScreenBounds(window, palette.CardSurface);
                    var work = WindowChrome.GetWorkArea(window);
                    Check($"custom palette stays on screen {theme}-{scale}", bounds.X >= work.X - 1 && bounds.Y >= work.Y - 1
                        && bounds.X + bounds.Width <= work.X + work.Width + 1 && bounds.Y + bounds.Height <= work.Y + work.Height + 1);
                }
                window.ApplyScale(100); await Task.Delay(60);
                await BoardWindowRegression.SaveVisual(palette, Path.Combine(directory, "palette-" + theme + ".png"));

                services.Settings.Canvas = services.Settings.Clone().Canvas;
                services.ApplySettings();
                var settings = new SettingsWindow(services, shell: shell);
                settings.Activate(); settings.SelectCategory("书写与擦除"); await Task.Delay(80);
                var toggle = Descendants(settings.Content).OfType<ToggleSwitch>()
                    .Single(control => AutomationProperties.GetName(control) == "启用自定义颜色色板");
                toggle.IsOn = false;
                Check("settings switch hides the custom palette without deleting colours " + theme,
                    !services.Store.Load().Canvas.EnableCustomColorPalette && services.Store.Load().Canvas.CustomColors.Count == 21
                    && !Descendants(custom).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "添加自定义颜色"));
                toggle.IsOn = true;
                Check("enabling restores the saved custom palette " + theme,
                    Descendants(custom).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "添加自定义颜色"));
                settings.Close(); window.Close();

                shell.OpenPenPalette(true); await Task.Delay(80);
                window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                palette = Descendants(window.Content).OfType<PenPalette>().Single();
                Invoke(Button(palette, "添加自定义颜色"));
                editor = await Editor();
                Descendants(editor.Content).OfType<ColorPicker>().Single().Color = Windows.UI.Color.FromArgb(255, 80, 200, 120);
                Invoke(Button(editor.Content, "保存自定义颜色"));
                await Wait(() => !shell.OpenWindows.Contains(window));
                stored = services.Store.Load().Canvas;
                Check("highlighter shares custom colours and retains its opacity " + theme,
                    stored.CustomColors.Count == 22 && stored.HighlighterColor == 0xff50c878
                    && services.OverlayInk.CurrentAttributes.HighlighterAlpha == services.Settings.Canvas.InkAlpha);

                shell.ToggleBoard(); await Task.Delay(100);
                var board = shell.OpenWindows.OfType<BoardWindow>().Single();
                board.ShowPenPalette(); await Task.Delay(80);
                var boardPalette = board.ToolPanels.SelectMany(panel => Descendants(panel.Content)).OfType<BoardPenPalette>().Single();
                var penColors = Descendants(boardPalette).OfType<InkColorPalette>().Single(view => AutomationProperties.GetName(view) == "画笔色板");
                Invoke(Descendants(penColors).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("自定义颜色 1 ·")));
                Check("whiteboard uses the same stored colours " + theme,
                    board.CurrentController.CurrentAttributes.Color.PackedArgb == 0xff2468ac && services.Store.Load().Canvas.PenColor == 0xff2468ac);
                board.Close(); await Task.Delay(80);
            }
            var pdf = services.Modules.GetExport<IPdfWorkspaceService>();
            string pdfPath = Path.Combine(directory, "custom-colours.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics").GetMethod("CreateFixture",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { pdfPath, 2 });
            await shell.OpenPdfAsync(pdfPath); await Task.Delay(100);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            workspace.Window.Activate();
            NativeMethods.SetForegroundWindow(WindowChrome.GetHwnd(workspace.Window));
            await Task.Delay(250);
            bool suppliedPdfForeground = NativeMethods.GetForegroundWindow() != WindowChrome.GetHwnd(workspace.Window);
            if (suppliedPdfForeground) shell.RefreshDocumentTarget(WindowChrome.GetHwnd(workspace.Window));
            checks.Add(new { check = "PDF palette uses the document toolbar target",
                passed = ReferenceEquals(shell.OpenWindows.OfType<FloatingBarWindow>().Single().Bar.Controller, workspace.Controller),
                foregroundContextSuppliedByTest = suppliedPdfForeground });
            shell.OpenPenPalette(false); await Task.Delay(80);
            var pdfPalette = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
            Invoke(Button(pdfPalette.Content, "添加自定义颜色"));
            var pdfEditor = await Editor();
            Descendants(pdfEditor.Content).OfType<ColorPicker>().Single().Color = Windows.UI.Color.FromArgb(255, 233, 50, 100);
            await Task.Delay(80);
            Invoke(Button(pdfEditor.Content, "保存自定义颜色"));
            await Wait(() => services.Settings.Canvas.CustomColors.LastOrDefault() == 0xffe93264);
            checks.Add(new { check = "PDF annotation edits and persists the shared custom palette",
                passed = workspace.Controller.CurrentAttributes.Color.PackedArgb == 0xffe93264 && services.Store.Load().Canvas.PenColor == 0xffe93264
                    && services.Store.Load().Canvas.CustomColors.Last() == 0xffe93264,
                actual = workspace.Controller.CurrentAttributes.Color.PackedArgb, stored = services.Store.Load().Canvas.PenColor,
                overlay = services.OverlayInk.CurrentAttributes.Color.PackedArgb, custom = services.Store.Load().Canvas.CustomColors.Last(),
                documentTarget = ReferenceEquals(shell.OpenWindows.OfType<FloatingBarWindow>().Single().Bar.Controller, workspace.Controller) });
            workspace.Window.Close();
        }
        catch (Exception error) { checks.Add(new { check = "custom colours exception", passed = false, error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "custom-colors.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }

        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        async Task<ColorEditorWindow> Editor()
        {
            await Wait(() => shell.OpenWindows.OfType<ColorEditorWindow>().Any());
            await Task.Delay(100);
            return shell.OpenWindows.OfType<ColorEditorWindow>().Single();
        }
    }

    private static async Task Wait(Func<bool> ready)
    {
        for (int count = 0; count < 100; count++) { if (ready()) return; await Task.Delay(30); }
        throw new TimeoutException("Custom colour interaction did not settle.");
    }
    private static Button Button(DependencyObject root, string name) => Descendants(root).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
