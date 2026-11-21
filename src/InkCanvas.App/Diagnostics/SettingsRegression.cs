using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class SettingsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, Guid.NewGuid().ToString("N")));
        using var updater = new UpdateCoordinator(services);
        var bar = new FloatingBarWindow();
        bar.Bar.Settings = services.Settings;
        bar.Bar.Controller = services.OverlayInk;
        bar.Bar.Build();
        WindowChrome.ShowWithoutActivation(bar);
        SettingsWindow window = null;
        try
        {
            string original = JsonSerializer.Serialize(services.Settings);
            Open();
            await Task.Delay(600);
            Check("opening settings preserves stored values", JsonSerializer.Serialize(services.Settings) == original);
            Check("settings permit keyboard activation", !NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_NOACTIVATE));
            var searchTimer = System.Diagnostics.Stopwatch.StartNew();
            var searchHits = window.SearchSettings("荧光笔");
            Check("search indexes real setting labels across categories", searchHits.Any(hit => hit.Title == "荧光笔粗细")
                && searchHits.Any(hit => hit.Title == "荧光笔透明度"));
            Check("search indexing preserves settings and selected category", JsonSerializer.Serialize(services.Settings) == original
                && Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == "启动选项");
            Check("search index builds without blocking the window", searchTimer.ElapsedMilliseconds < 1500);
            Check("search includes choice values", window.SearchSettings("深色").Any(hit => hit.Title == "颜色主题"));
            Check("search includes ink hold and hardware brush settings", window.SearchSettings("停笔").Any()
                && window.SearchSettings("硬件压感").Any() && window.SearchSettings("方向吸附").Any());
            var highlighterMatches = window.SearchSettings("画板 荧光笔");
            Check("search handles case and category terms", window.SearchSettings("powerpoint").Any()
                && highlighterMatches.Any(hit => hit.Title == "荧光笔粗细")
                && highlighterMatches.Any(hit => hit.Title == "荧光笔透明度")
                && highlighterMatches.All(hit => hit.Category == "画板选项"));
            Check("search missing and empty queries have no matches", window.SearchSettings("不存在的设置条目").Count == 0
                && window.SearchSettings(" ").Count == 0);
            var widthHit = searchHits.Single(hit => hit.Title == "荧光笔粗细");
            Check("search result opens matching category", window.NavigateToSetting(widthHit)
                && Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == "画板选项");
            ((FrameworkElement)window.Content).UpdateLayout();
            await Task.Delay(100);
            Check("search result exposes original editable setting", Find<Slider>("荧光笔粗细") is { IsEnabled: true });
            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "settings-search-result.png"));
            Check("settings uses shared WinUI title bar", window.Content is WindowFrame { Header.Title: "Ink Canvas 设置" }
                && window.ExtendsContentIntoTitleBar);
            Check("update search includes release list entry", window.SearchSettings("版本列表").Any());
            window.SelectCategory("关于 Ink Canvas Artistry");
            ((FrameworkElement)window.Content).UpdateLayout();
            Check("release list and update status are visible in settings", Descendants(window.Content).OfType<TextBlock>().Any(text => text.Text == "版本列表")
                && Descendants(window.Content).OfType<TextBlock>().Any(text => text.Text == updater.Status));
            var searchBox = Descendants(window.Content).OfType<AutoSuggestBox>().Single();
            searchBox.Text = "双指缩放";
            ((FrameworkElement)window.Content).UpdateLayout();
            Invoke(Descendants(searchBox).OfType<Button>().Single(button => button.Name == "QueryButton"));
            await Task.Delay(80);
            Check("search button navigates through the normal query event", Descendants(window.Content).OfType<WindowWorkspace>().Single().SelectedPage == "手势选项"
                && Find<ToggleSwitch>("双指缩放") is not null);
            var originalSize = window.AppWindow.ClientSize;
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(640, 480)); await Task.Delay(100);
            var navigation = Descendants(window.Content).OfType<NavigationView>().Single();
            var workspace = Descendants(window.Content).OfType<WindowWorkspace>().Single();
            workspace.FocusSearch(); await Task.Delay(100);
            var focused = FocusManager.GetFocusedElement(((FrameworkElement)window.Content).XamlRoot);
            Check("search focus opens narrow navigation and reaches search editor", navigation.IsPaneOpen
                && Descendants(searchBox).Any(element => ReferenceEquals(element, focused)));
            window.NavigateToSetting(widthHit);
            Check("narrow-window search closes overlay navigation to reveal setting", !navigation.IsPaneOpen);
            window.AppWindow.ResizeClient(originalSize);
            searchBox.Text = "不存在的设置条目";
            Invoke(Descendants(searchBox).OfType<Button>().Single(button => button.Name == "QueryButton"));
            Check("missing search query provides visible feedback", Descendants(window.Content).OfType<InfoBar>().Any(info => info.IsOpen && info.Message == "没有找到相关设置"));

            var toggles = new (string Label, Func<bool> Get)[]
            {
                ("上传图片时将画质压缩至 1080P 及以下", () => services.Settings.Canvas.IsCompressPicturesUploaded),
                ("浮动工具栏显示文字注释", () => services.Settings.Appearance.IsEnableDisPlayFloatBarText),
                ("浮动工具栏背景色", () => services.Settings.Appearance.IsColorfulViewboxFloatingBar),
                ("按钮背景透明", () => services.Settings.Appearance.IsTransparentButtonBackground),
                ("显示退出按钮", () => services.Settings.Appearance.IsShowExitButton),
                ("显示橡皮按钮", () => services.Settings.Appearance.IsShowEraserButton),
                ("显示隐藏控制按钮", () => services.Settings.Appearance.IsShowHideControlButton),
                ("显示左右切换按钮", () => services.Settings.Appearance.IsShowLRSwitchButton),
                ("显示手指模式切换按钮", () => services.Settings.Appearance.IsShowModeFingerToggleSwitch),
                ("显示画笔光标", () => services.Settings.Canvas.IsShowCursor),
                ("在调色盘窗口中显示 启用笔尖模式 切换按钮", () => services.Settings.Appearance.IsEnableDisPlayNibModeToggler),
                ("关闭软件时二次弹窗确认", () => services.Settings.Advanced.IsSecondConfimeWhenShutdownApp),
                ("进入“希沃轻白板”时自动收纳", () => services.Settings.Automation.IsAutoFoldInEasiNote3C),
                ("进入“鸿合视频展台”时自动收纳", () => services.Settings.Automation.IsAutoFoldInHiteCamera),
                ("进入“文香白板”时自动收纳", () => services.Settings.Automation.IsAutoFoldInWxBoardMain),
                ("进入“中原旧白板”时自动收纳", () => services.Settings.Automation.IsAutoFoldInOldZyBoard),
                ("自动查杀希沃“PPT 小工具”", () => services.Settings.Automation.IsAutoKillPptService),
                ("自动查杀 希沃白板5", () => services.Settings.Automation.IsAutoKillEasiNote),
                ("切换到鼠标模式后自动清屏", () => services.Settings.Automation.IsAutoClearWhenExitingWritingMode),
            };
            foreach (var item in toggles)
            {
                var toggle = Find<ToggleSwitch>(item.Label);
                bool before = item.Get();
                var peer = new ToggleSwitchAutomationPeer(toggle);
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                bool changed = item.Get() != before;
                Check(item.Label, changed && toggle.IsOn == item.Get());
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                Check(item.Label + " restores", item.Get() == before);
            }
            var autoUpdate = Find<ToggleSwitch>("自动检查更新");
            bool updateBefore = autoUpdate.IsOn;
            autoUpdate.IsOn = false;
            Check("silent update disables with a visible reason", !Find<ToggleSwitch>("静默更新").IsEnabled
                && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(Find<ToggleSwitch>("静默更新"))));
            autoUpdate = Find<ToggleSwitch>("自动检查更新");
            autoUpdate.IsOn = true;
            Check("silent update enables when its prerequisite returns", Find<ToggleSwitch>("静默更新").IsEnabled);
            Find<ToggleSwitch>("自动检查更新").IsOn = updateBefore;
            Find<ToggleSwitch>("进入“希沃白板”时自动收纳").IsOn = false;
            Find<ToggleSwitch>("进入“希沃轻白板”时自动收纳").IsOn = false;
            Check("annotation exception requires a parent autofold rule", !Find<ToggleSwitch>("自动收纳忽略桌面EN批注窗口").IsEnabled);
            Find<ToggleSwitch>("进入“希沃轻白板”时自动收纳").IsOn = true;
            Check("annotation exception enables immediately", Find<ToggleSwitch>("自动收纳忽略桌面EN批注窗口").IsEnabled);
            Find<ToggleSwitch>("进入“希沃轻白板”时自动收纳").IsOn = false;
            string missingPath = Path.Combine(directory, "missing-cleanup-directory");
            Find<TextBox>("墨迹、截图自动保存位置").Text = missingPath;
            await Task.Delay(450);
            Check("cleanup disables when saved directory is missing", !Find<ToggleSwitch>("定期自动删除超过保存时间的墨迹、截图文件").IsEnabled);
            Directory.CreateDirectory(missingPath);
            await Task.Delay(2200);
            Check("cleanup enables when saved directory becomes available", Find<ToggleSwitch>("定期自动删除超过保存时间的墨迹、截图文件").IsEnabled);
            Find<ToggleSwitch>("定期自动删除超过保存时间的墨迹、截图文件").IsOn = true;
            var retention = Find<ComboBox>("保存时间设置（Days）");
            retention.SelectedIndex = 3;
            Check("retention days save through the real combo", services.Settings.Automation.AutoDelSavedFilesDaysThreshold == 7);
            Find<ToggleSwitch>("定期自动删除超过保存时间的墨迹、截图文件").IsOn = false;
            Check("retention editor disables when cleanup is off", !Find<ComboBox>("保存时间设置（Days）").IsEnabled);
            var showEraser = Find<ToggleSwitch>("显示橡皮按钮");
            showEraser.IsOn = false;
            Check("eraser visibility applies immediately", bar.Bar.GetToolButton(InkTool.Eraser).Visibility == Visibility.Collapsed);
            showEraser.IsOn = true;
            Check("eraser visibility restores", bar.Bar.GetToolButton(InkTool.Eraser).Visibility == Visibility.Visible);

            var eraser = Find<ComboBox>("橡皮模式");
            eraser.SelectedIndex = 2;
            Check("whole stroke eraser applies", services.OverlayInk.EraserMode == InkEraserMode.WholeStroke);
            eraser.SelectedIndex = 1;
            Check("area eraser applies", services.OverlayInk.EraserMode == InkEraserMode.Area);
            eraser.SelectedIndex = 0;
            Find<ToggleSwitch>("使用硬件压感").IsOn = false;
            Check("hardware pressure setting reaches live controller", !services.OverlayInk.UseHardwarePressure);
            Find<ToggleSwitch>("使用硬件压感").IsOn = true;
            Find<Slider>("停笔吸附等待时间 (毫秒)").Value = 800;
            Check("hold duration reaches live controller", services.OverlayInk.RecognitionOptions.HoldMilliseconds == 800);
            Find<ToggleSwitch>("自动对齐水平、垂直与 45° 方向").IsOn = false;
            Check("alignment editor follows its toggle", !Find<Slider>("方向吸附容差 (度)").IsEnabled
                && !services.OverlayInk.RecognitionOptions.AutoAlign);
            Find<ToggleSwitch>("自动对齐水平、垂直与 45° 方向").IsOn = true;
            Find<Slider>("方向吸附容差 (度)").Value = 7;
            Check("alignment tolerance reaches live controller", services.OverlayInk.RecognitionOptions.AlignmentAngle == 7);
            var palette = (EraserPalette)bar.Bar.CreateEraserPanelContent();
            var lasso = Descendants(palette).OfType<Button>().Single(button => AutomationProperties.GetName(button).Contains("套索擦"));
            Invoke(lasso);
            services.ApplySettings();
            Check("manual eraser choice survives other settings", services.OverlayInk.EraserMode == InkEraserMode.Lasso);

            var boardColor = Find<ToggleSwitch>("白板背景配色");
            boardColor.IsOn = true;
            Check("whiteboard color applies", services.Board.Background == BoardBackground.White);
            boardColor.IsOn = false;
            Check("blackboard color applies", services.Board.Background == BoardBackground.Black);

            CheckShortcut("工具栏缩放快捷值 125%", "浮动工具栏 UI 缩放 (%)", 125, () => services.Settings.Appearance.FloatingBarScale);
            CheckShortcut("画板缩放快捷值 80%", "画板 UI 缩放 (%)", 80, () => services.Settings.Appearance.BlackboardScale);
            Find<Slider>("浮动工具栏底部边距 (像素)").Value = 40;
            CheckShortcut("底部边距快捷值 重置为 100", "浮动工具栏底部边距 (像素)", 100, () => services.Settings.Appearance.FloatingBarBottomMargin);

            Find<Slider>("荧光笔粗细").Value = 120;
            services.OverlayInk.Tool = InkTool.Highlighter;
            Check("extended highlighter width applies", services.OverlayInk.CurrentAttributes.BaseWidth == 60);
            services.OverlayInk.Tool = InkTool.Pen;
            Check("highlighter width does not change pen", services.OverlayInk.CurrentAttributes.BaseWidth == services.Settings.Canvas.InkWidth);
            var alpha = Find<Slider>("荧光笔透明度");
            alpha.Value = 180;
            Check("highlighter alpha applies", services.OverlayInk.CurrentAttributes.HighlighterAlpha == 180);
            window.SelectCategory("画板选项");
            var penStyles = Find<ComboBox>("笔迹风格");
            penStyles.SelectedIndex = 7;
            penStyles.IsDropDownOpen = true;
            await Task.Delay(100);
            bool inkWashIconVisible = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<PenStyleIcon>()
                .Any(icon => AutomationProperties.GetName(icon) == "水墨笔锋");
            penStyles.IsDropDownOpen = false;
            checks.Add(new { check = "ink wash setting shows its icon and applies", selectedIndex = penStyles.SelectedIndex,
                persistedIndex = services.Settings.Canvas.InkStyle, liveStyle = services.OverlayInk.CurrentAttributes.TaperKind.ToString(),
                hasIcon = inkWashIconVisible,
                passed = penStyles.SelectedIndex == 7 && services.Settings.Canvas.InkStyle == 7
                    && services.OverlayInk.CurrentAttributes.TaperKind == InkCanvas.Core.Ink.TaperKind.InkWash
                    && inkWashIconVisible });
            penStyles.SelectedIndex = 0;
            var path = Find<TextBox>("墨迹、截图自动保存位置");
            path.Text = Path.Combine(directory, "typed-path");
            await Task.Delay(450);
            Check("typed path automatically saves", services.Store.Load().Automation.AutoSavedStrokesLocation == path.Text);
            Invoke(Find<Button>("将自动保存目录设置为文档目录"));
            Check("path shortcut refreshes editor", path.Text == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

            foreach (int themeIndex in new[] { 1, 0, 2 })
            {
                Find<ComboBox>("颜色主题").SelectedIndex = themeIndex;
                await Task.Delay(100);
                Check("theme rebuild preserves edited values " + themeIndex,
                    Find<Slider>("浮动工具栏 UI 缩放 (%)").Value == 125
                    && Find<Slider>("荧光笔透明度").Value == 180);
            }

            string lastPath = Path.Combine(directory, "close-before-debounce");
            Find<TextBox>("墨迹、截图自动保存位置").Text = lastPath;
            window.Close();
            window = null;
            Check("closing flushes pending text", services.Store.Load().Automation.AutoSavedStrokesLocation == lastPath);
            string persisted = JsonSerializer.Serialize(services.Store.Load());
            Check("all edited values persist", persisted == JsonSerializer.Serialize(services.Settings));
            Open();
            await Task.Delay(150);
            var restoredEraserMode = Find<ComboBox>("橡皮模式").SelectedIndex;
            checks.Add(new { check = "reopening restores saved controls", lastPathRestored = Find<TextBox>("墨迹、截图自动保存位置").Text == lastPath,
                toolbarScale = Find<Slider>("浮动工具栏 UI 缩放 (%)").Value, eraserMode = restoredEraserMode,
                passed = Find<TextBox>("墨迹、截图自动保存位置").Text == lastPath
                    && Find<Slider>("浮动工具栏 UI 缩放 (%)").Value == 125 && restoredEraserMode == 3 });
            foreach (int themeIndex in new[] { 0, 1 })
            {
                Find<ComboBox>("颜色主题").SelectedIndex = themeIndex;
                await Task.Delay(120);
                foreach (int width in new[] { 1000, 640, 1280 })
                {
                    window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, 760));
                    await Task.Delay(100);
                    foreach (string category in new[] { "启动选项", "画板选项", "手势选项", "显示器", "墨迹识别选项", "外观选项", "PPT 选项", "高级选项", "自动选项", "实验性设置", "模块与插件", "关于 Ink Canvas Artistry", "抽奖与点名" })
                    {
                        window.SelectCategory(category);
                        ((FrameworkElement)window.Content).UpdateLayout();
                        await Task.Delay(40);
                        var rows = Descendants(window.Content).OfType<SettingsRow>().ToArray();
                        Check("unavailable settings explain their prerequisite " + category, rows.All(row =>
                            !Descendants(row.Editor).OfType<Control>().Any(control => !control.IsEnabled)
                            || !string.IsNullOrWhiteSpace(row.Description)));
                        var invalid = rows.Where(row => row.Children.OfType<FrameworkElement>().Any(child =>
                        {
                            var bounds = child.TransformToVisual(row).TransformBounds(new Windows.Foundation.Rect(0, 0, child.ActualWidth, child.ActualHeight));
                            return bounds.Left < -1 || bounds.Top < -1 || bounds.Right > row.ActualWidth + 1 || bounds.Bottom > row.ActualHeight + 1;
                        })).Select(row => row.Title).ToArray();
                        checks.Add(new { check = "settings rows fit responsive page", themeIndex, width, category,
                            passed = invalid.Length == 0, invalid, rows = rows.Length, stacked = rows.Count(row => row.IsStacked) });
                        if (category == "外观选项")
                        {
                            Check("scale presets share the slider card " + themeIndex + " " + width, rows
                                .Where(row => row.Title.Contains("UI 缩放")).All(row => Descendants(row).OfType<Button>().Count() == 5));
                        }
                        if (width is 640 or 1280 && category is "启动选项" or "外观选项" or "画板选项")
                            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, $"settings-{category}-{themeIndex}-{width}.png"));
                    }
                }
            }

            void Open()
            {
                window = new SettingsWindow(services, updater);
                window.SettingsChanged += bar.Bar.RefreshSettingsVisuals;
                window.Activate();
            }
            T Find<T>(string label) where T : DependencyObject
            {
                T FindOnPage() => Descendants(window.Content).OfType<T>().SingleOrDefault(control => AutomationProperties.GetName(control) == label);
                var result = FindOnPage();
                if (result is not null) return result;
                foreach (string category in new[] { "启动选项", "画板选项", "手势选项", "墨迹识别选项", "外观选项", "PPT 选项", "高级选项", "自动选项", "抽奖与点名" })
                {
                    window.SelectCategory(category);
                    ((FrameworkElement)window.Content).UpdateLayout();
                    result = FindOnPage();
                    if (result is not null) return result;
                }
                throw new InvalidOperationException("Missing setting " + label);
            }
            void Check(string name, bool passed) => checks.Add(new { check = name, passed });
            void CheckShortcut(string button, string slider, double expected, Func<double> get)
            {
                Invoke(Find<Button>(button));
                Check(button, Find<Slider>(slider).Value == expected && get() == expected);
            }
        }
        catch (Exception exception) { checks.Add(new { check = "settings regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            window?.Close();
            bar.Close();
            File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
