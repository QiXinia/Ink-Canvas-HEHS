using System.Net;
using System.Text;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Update;
using InkCanvas.Core.Versioning;
using InkCanvas.Features.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class UpdateUiRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        try
        {
            await Task.Delay(200);
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            shell.ToggleFold();
            var current = shell.Updater.CurrentVersion;
            var nextVersion = new AppVersion(current.Major, current.Minor, current.Patch + 1, 0);
            string notes = """
                ## 书写与几何

                支持 **粗体**、*斜体*、~~删除线~~ 与 `InkCanvas.exe`，保留 A &amp; B。

                - 改进触摸书写
                  - 嵌套条目
                - [x] 已完成
                - [ ] 待完成

                1. 下载更新
                2. 重新启动

                > 引用中的 **提示**

                ```csharp
                Console.WriteLine("**保留代码原文**");
                ```

                | 功能 | 状态 |
                | --- | --- |
                | Markdown | 可用 |

                [发行页](https://github.com/QiXinia/Ink-Canvas-HEHS/releases)
                [脚本链接](javascript:alert(1))

                ---

                """ + string.Join("\n\n", Enumerable.Range(1, 40).Select(index => $"第 {index} 项更新说明，改进触摸书写和文档批注体验。"));
            var release = new ReleaseInfo("v" + nextVersion, nextVersion, "Ink Canvas WinUI 3", notes,
                "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/tag/v" + nextVersion,
                "https://github.com/QiXinia/Ink-Canvas-HEHS/releases/download/v" + nextVersion + "/InkCanvas-test.zip",
                128 * 1024 * 1024) { AssetName = "InkCanvas-test.zip" };
            var same = release with { Tag = "v" + current, Version = current };
            var old = release with { Tag = "v1.0.0", Version = new(1, 0, 0, 0) };
            foreach (int theme in new[] { 1, 2, 0 })
            {
                services.Settings.Appearance.Theme = theme;
                var confirmation = shell.Updater.ConfirmRelease(release);
                await Task.Delay(160);
                if (confirmation.IsFaulted) await confirmation;
                var window = shell.OpenWindows.OfType<UpdateWindow>().Single();
                var root = (FrameworkElement)window.Content;
                Check("confirmation uses native WinUI frame theme " + theme, window.ExtendsContentIntoTitleBar && root is WindowFrame
                    && (theme == 0 || root.ActualTheme == (theme == 2 ? ElementTheme.Dark : ElementTheme.Light)));
                Check("confirmation works with folded toolbar theme " + theme, bar.IsFolded && window.AppWindow.IsVisible
                    && !ReferenceEquals(root.XamlRoot, ((FrameworkElement)bar.Content).XamlRoot));
                Check("full notes and download size appear theme " + theme, Texts(window).Contains("第 40 项") && Texts(window).Contains("128 MB"));
                var richText = Descendants(root).OfType<RichTextBlock>().ToArray();
                var inlines = richText.SelectMany(text => text.Blocks.OfType<Paragraph>().SelectMany(paragraph => InlineDescendants(paragraph.Inlines))).ToArray();
                Check("Markdown heading has native semantics theme " + theme, richText.Any(text => AutomationProperties.GetHeadingLevel(text) == AutomationHeadingLevel.Level2));
                Check("Markdown emphasis and code use native inline formatting theme " + theme,
                    inlines.OfType<Span>().Any(span => span.FontWeight.Weight >= 700)
                    && inlines.OfType<Span>().Any(span => span.FontStyle == Windows.UI.Text.FontStyle.Italic)
                    && inlines.OfType<Span>().Any(span => span.TextDecorations == Windows.UI.Text.TextDecorations.Strikethrough)
                    && inlines.OfType<Run>().Any(run => run.Text == "InkCanvas.exe" && run.FontFamily.Source == "Consolas"));
                Check("Markdown links allow browser navigation and retain blocked link text theme " + theme,
                    inlines.OfType<Hyperlink>().Any(link => link.NavigateUri?.Host == "github.com")
                    && inlines.OfType<Hyperlink>().All(link => link.NavigateUri?.Scheme is "https" or "http" or "mailto")
                    && Texts(window).Contains("脚本链接"));
                Check("Markdown nested lists tasks tables and entities render theme " + theme,
                    Texts(window).Contains("嵌套条目") && Texts(window).Contains("☑") && Texts(window).Contains("☐")
                    && Texts(window).Contains("Markdown") && Texts(window).Contains("A & B"));
                Check("Markdown fenced code preserves literal markup theme " + theme, Descendants(root).OfType<TextBlock>()
                    .Any(text => text.Text == "Console.WriteLine(\"**保留代码原文**\");" && text.FontFamily.Source == "Consolas"));
                var scroll = Descendants(root).OfType<ScrollViewer>().Single(item => AutomationProperties.GetName(item) == "更新内容");
                Check("long notes scroll inside the window theme " + theme, scroll.ScrollableHeight > 0);
                var accept = Button(window, "下载并更新");
                Check("confirmation has an accent action within client bounds theme " + theme,
                    ReferenceEquals(accept.Style, Application.Current.Resources["AccentButtonStyle"]) && IsInside(accept, root));
                await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "update-confirm-" + theme + ".png"));
                double dpi = root.XamlRoot.RasterizationScale;
                window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(480 * dpi), (int)(520 * dpi)));
                await Task.Delay(100);
                Check("small update window keeps actions accessible theme " + theme, IsInside(accept, root) && IsInside(Button(window, "稍后"), root));
                Invoke(accept);
                Check("accept returns through the update callback theme " + theme, await confirmation.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            var selection = shell.Updater.ChooseRelease(new[] { release, same, old });
            await Task.Delay(150);
            var versions = shell.OpenWindows.OfType<UpdateWindow>().Single();
            var combo = Descendants(versions.Content).OfType<ComboBox>().Single();
            Check("version selector identifies the current release", combo.Items[1].ToString().Contains("当前版本"));
            combo.SelectedIndex = 1;
            Check("current version disables update with a reason", !Button(versions, "下载并更新").IsEnabled
                && Descendants(versions.Content).OfType<InfoBar>().Single().Message.Contains("正在使用"));
            combo.SelectedIndex = 2;
            Check("older version disables update with a reason", !Button(versions, "下载并更新").IsEnabled
                && Descendants(versions.Content).OfType<InfoBar>().Single().Message.Contains("早于"));
            combo.SelectedIndex = 0;
            await BoardWindowRegression.SaveVisual((FrameworkElement)versions.Content, Path.Combine(directory, "update-versions.png"));
            Invoke(Button(versions, "下载并更新"));
            Check("version selector returns the chosen release", await selection.WaitAsync(TimeSpan.FromSeconds(3)) == release);
            var empty = shell.Updater.ChooseRelease(Array.Empty<ReleaseInfo>()); await Task.Delay(100);
            var emptyWindow = shell.OpenWindows.OfType<UpdateWindow>().Single();
            Check("empty version list keeps update disabled", !Button(emptyWindow, "下载并更新").IsEnabled);
            emptyWindow.Close();
            Check("caption close cancels version selection", await empty.WaitAsync(TimeSpan.FromSeconds(3)) is null);
            var notice = shell.Updater.Notify("更新失败", "所有更新源暂时无法连接。\n请稍后重新检查更新。");
            await Task.Delay(120);
            var noticeWindow = shell.OpenWindows.OfType<UpdateWindow>().Single();
            Check("update error uses the same native frame", noticeWindow.Content is WindowFrame && Texts(noticeWindow).Contains("所有更新源"));
            await BoardWindowRegression.SaveVisual((FrameworkElement)noticeWindow.Content, Path.Combine(directory, "update-error.png"));
            Invoke(Button(noticeWindow, "知道了")); await notice.WaitAsync(TimeSpan.FromSeconds(3));
            Check("closing update notice releases the window", !shell.OpenWindows.OfType<UpdateWindow>().Any());

            using var service = new UpdateService(transport: new UpdateTransport(new ReleaseHandler(release)));
            using var coordinator = new UpdateCoordinator(services, service)
            {
                ConfirmRelease = shell.Updater.ConfirmRelease,
                Confirm = (_, _) => throw new InvalidOperationException("Legacy confirmation should not be used."),
            };
            var check = coordinator.CheckAsync(true);
            for (int attempt = 0; attempt < 50 && !shell.OpenWindows.OfType<UpdateWindow>().Any(); attempt++) await Task.Delay(50);
            await Task.Delay(120);
            var pending = shell.OpenWindows.OfType<UpdateWindow>().Single();
            Check("interactive update enters native confirmation with complete notes", Texts(pending).Contains("第 40 项"));
            Invoke(Button(pending, "稍后")); await check.WaitAsync(TimeSpan.FromSeconds(3));
            Check("postponing update preserves cancellation behavior", coordinator.Progress.Stage == UpdateStage.Canceled && !coordinator.IsBusy);
        }
        catch (Exception ex) { checks.Add(new { check = "update UI regression exception", pass = false, detail = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "update-ui.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            var windows = shell.OpenWindows.ToArray();
            shell.Dispose();
            foreach (var window in windows) if (window is not UpdateWindow) window.Close();
            Application.Current.Exit();
        }
        void Check(string check, bool pass) => checks.Add(new { check, pass });
    }

    private sealed class ReleaseHandler(ReleaseInfo release) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string json = JsonSerializer.Serialize(new[] { new { tag_name = release.Tag, name = release.Name, body = release.Notes,
                html_url = release.PageUrl, assets = new[] { new { name = "InkCanvas-WinUI3-test-win-x64.zip", browser_download_url = release.InstallerUrl, size = release.InstallerSize } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private static Button Button(Window window, string label) => Descendants(window.Content).OfType<Button>().Single(button => button.Content?.ToString() == label);
    private static string Texts(Window window) => string.Join(" ", Descendants(window.Content).SelectMany(element => element switch
    {
        TextBlock text => new[] { text.Text },
        RichTextBlock text => text.Blocks.OfType<Paragraph>().Select(paragraph => string.Concat(InlineDescendants(paragraph.Inlines).OfType<Run>().Select(run => run.Text))),
        _ => Array.Empty<string>(),
    }));
    private static IEnumerable<Inline> InlineDescendants(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var child in InlineDescendants(span.Inlines)) yield return child;
        }
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static bool IsInside(FrameworkElement child, FrameworkElement root)
    {
        var origin = child.TransformToVisual(root).TransformPoint(default);
        return child.ActualWidth >= 100 && child.ActualHeight >= 44 && origin.X >= 0 && origin.Y >= 0
            && origin.X + child.ActualWidth <= root.ActualWidth + 1 && origin.Y + child.ActualHeight <= root.ActualHeight + 1;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
