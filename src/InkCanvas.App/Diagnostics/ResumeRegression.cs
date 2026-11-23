using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class ResumeRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var anchor = new Window { Title = "续接功能验收", Content = new Grid() };
        WindowChrome.MakeStandard(anchor, 360, 160);
        WindowChrome.ShowWithoutActivation(anchor);
        try
        {
            var closedWindow = new Window { Content = new Grid() };
            WindowChrome.MakeBorderless(closedWindow);
            WindowChrome.ShowWithoutActivation(closedWindow);
            closedWindow.Close();
            WindowChrome.EnsureAbove(closedWindow);
            Check("overlay chrome ignores closed windows", true);
            await BoardCase(ResumeBehavior.Automatic, 1);
            await BoardCase(ResumeBehavior.Reminder, 2);
            await BoardCase(ResumeBehavior.Reminder, 1, accept: true);
            await PdfCase();
        }
        catch (Exception error) { checks.Add(new { check = "resume exception", passed = false, error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "resume.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            anchor.Close();
            Application.Current.Exit();
        }

        void Check(string name, bool passed) => checks.Add(new { check = name, passed });

        async Task BoardCase(ResumeBehavior behavior, int theme, bool accept = false)
        {
            using var services = Services(Path.Combine(directory, "board-" + behavior + "-" + accept));
            services.Settings.Resume.Whiteboard = behavior;
            services.Settings.Appearance.Theme = theme;
            services.Board.Current.Document.Add(Stroke());
            services.Board.NextPage(); services.Board.Background = BoardBackground.Grid;
            services.Board.Current.Document.Add(Stroke());
            var view = Matrix3x2.CreateScale(1.2f) * Matrix3x2.CreateTranslation(-100, -80);
            services.Board.Current.ViewTransform = view;
            await services.BoardResume.SaveAsync();
            var record = await services.BoardResume.Store.ReadAsync();
            await File.WriteAllBytesAsync(services.BoardResume.Store.PathName, JsonSerializer.SerializeToUtf8Bytes(record with { BootId = record.BootId - 20 }));
            services.Board.RemoveCurrentPage(); services.Board.ClearCurrentPage();
            using var shell = new ShellController(services);
            shell.Start(); await shell.InitializeResumeAsync(true); await Task.Delay(150);
            if (behavior == ResumeBehavior.Automatic)
            {
                var board = shell.OpenWindows.OfType<BoardWindow>().Single();
                Check("automatic whiteboard resumes page and paper after simulated reboot", services.Board.PageCount == 2 && services.Board.CurrentIndex == 1
                    && services.Board.Background == BoardBackground.Grid && board.CurrentController.Document.Count == 1);
                Check("automatic whiteboard resumes canvas position", board.CurrentController.ViewTransform == view);
                Check("resume stores no desktop annotation", services.OverlayInk.Document.Count == 0
                    && !Directory.GetFiles(Path.Combine(services.Store.Directory, "Resume")).Any(path => Path.GetFileName(path).StartsWith("desktop", StringComparison.Ordinal)));
            }
            else
            {
                var reminder = shell.OpenWindows.OfType<ResumeReminderWindow>().Single();
                Check("whiteboard reminder retains input focus and offers continuation", NativeMethods.HasExStyle(WindowChrome.GetHwnd(reminder), NativeMethods.WS_EX_NOACTIVATE)
                    && !shell.OpenWindows.OfType<BoardWindow>().Any());
                await BoardWindowRegression.SaveVisual((FrameworkElement)reminder.Content, Path.Combine(directory, "reminder-dark.png"));
                if (accept)
                {
                    Invoke(Descendants(reminder.Content).OfType<Button>().Single(button => (string)button.Content == "继续"));
                    await Task.Delay(200);
                    Check("whiteboard reminder continue restores page and content", shell.OpenWindows.OfType<BoardWindow>().Any()
                        && services.Board.CurrentIndex == 1 && services.Board.Current.Document.Count == 1);
                }
                else
                {
                    await Task.Delay(8300);
                    Check("whiteboard reminder expires without restoring", !shell.OpenWindows.OfType<ResumeReminderWindow>().Any() && !shell.OpenWindows.OfType<BoardWindow>().Any());
                    Check("expired reminder deletes previous whiteboard and progress", await services.BoardResume.Store.ReadAsync() is null
                        && Directory.GetFiles(Path.Combine(services.Store.Directory, "Resume"), "board-*.icink").Length == 0);
                }
            }
            CloseWindows(shell);
        }

        async Task PdfCase()
        {
            using var services = Services(Path.Combine(directory, "pdf"));
            services.Settings.Resume.Whiteboard = ResumeBehavior.Disabled;
            services.Settings.Resume.Pdf = ResumeBehavior.Automatic;
            using var shell = new ShellController(services); shell.Start();
            var pdf = services.Modules.GetExport<IPdfWorkspaceService>();
            var resume = services.Modules.GetExport<IPdfResumeService>();
            string source = Path.Combine(directory, "reading.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics").GetMethod("CreateFixture",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { source, 6 });
            await shell.OpenPdfAsync(source); await Task.Delay(80);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            GoTo(workspace, 3); await resume.FlushResumeAsync();
            Check("PDF reading page is persisted", (await new PdfReadingProgressStore(services.Store.Directory).FindAsync(source))?.PageIndex == 3);
            await workspace.CloseAsync();
            await shell.OpenPdfAsync(source); await Task.Delay(80);
            workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            Check("PDF reopens at last reading page", Page(workspace) == 3);
            await workspace.CloseAsync();
            services.Settings.Resume.Pdf = ResumeBehavior.Reminder;
            await shell.OpenPdfAsync(source); await Task.Delay(80);
            workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            var reminder = shell.OpenWindows.OfType<ResumeReminderWindow>().Single();
            Check("PDF reminder leaves first page ready to read", Page(workspace) == 0);
            Invoke(Descendants(reminder.Content).OfType<Button>().Single(button => (string)button.Content == "继续"));
            await Task.Delay(100);
            Check("PDF reminder continue returns to remembered page", Page(workspace) == 3);
            await workspace.CloseAsync();
            await shell.OpenPdfAsync(source); await Task.Delay(80);
            workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            reminder = shell.OpenWindows.OfType<ResumeReminderWindow>().Single();
            Invoke(Descendants(reminder.Content).OfType<Button>().Single(button => (string)button.Content == "忽略"));
            await Task.Delay(80);
            Check("PDF dismiss deletes old reading position", await new PdfReadingProgressStore(services.Store.Directory).FindAsync(source) is null);
            services.Settings.Resume.Pdf = ResumeBehavior.Disabled; await workspace.CloseAsync();
            await shell.OpenPdfAsync(source); await Task.Delay(80);
            workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            Check("disabled PDF resume starts at first page", Page(workspace) == 0);
            var settings = new SettingsWindow(services, null, shell); settings.Activate(); await Task.Delay(80);
            var navigation = Descendants(settings.Content).OfType<WindowWorkspace>().Single();
            navigation.SelectPage("常规"); await Task.Delay(50);
            var boardChoice = Descendants(settings.Content).OfType<ComboBox>().Single(combo => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(combo) == "重启电脑后的白板续接");
            boardChoice.SelectedIndex = 1;
            navigation.SelectPage("PDF 阅读与批注"); await Task.Delay(50);
            var choice = Descendants(settings.Content).OfType<ComboBox>().Single(combo => (string)combo.Header == "重新打开 PDF 时");
            choice.SelectedIndex = 2;
            Check("PDF plugin and board settings save independently", services.Store.Load().Resume.Whiteboard == ResumeBehavior.Automatic
                && services.Store.Load().Resume.Pdf == ResumeBehavior.Reminder);
            settings.Close(); await workspace.CloseAsync();
            services.Settings.Resume.Pdf = ResumeBehavior.Automatic;
            await new PdfReadingProgressStore(services.Store.Directory, 0).SaveAsync(source, 2);
            await resume.InitializeResumeAsync(true); await Task.Delay(100);
            workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            Check("PDF automatically reopens last document after simulated reboot", Page(workspace) == 2);
            await workspace.CloseAsync();
            services.Settings.Resume.Pdf = ResumeBehavior.Reminder;
            await new PdfReadingProgressStore(services.Store.Directory, 0).SaveAsync(source, 4);
            await resume.InitializeResumeAsync(true); await Task.Delay(80);
            reminder = shell.OpenWindows.OfType<ResumeReminderWindow>().Single();
            Invoke(Descendants(reminder.Content).OfType<Button>().Single(button => (string)button.Content == "继续"));
            await Task.Delay(400);
            workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            Check("PDF startup reminder opens the document at remembered page", Page(workspace) == 4);
            await workspace.CloseAsync();
            await shell.OpenPdfAsync(source); await Task.Delay(80);
            workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            await Task.Delay(8300);
            Check("PDF reminder expires and clears saved page", !shell.OpenWindows.OfType<ResumeReminderWindow>().Any()
                && await new PdfReadingProgressStore(services.Store.Directory).FindAsync(source) is null);
            await workspace.CloseAsync();
            CloseWindows(shell);
        }
    }

    private static void CloseWindows(ShellController shell)
    {
        var windows = shell.OpenWindows.Where(window => window is not BoardWindow).ToArray();
        shell.Dispose();
        foreach (var window in windows) if (window.AppWindow is not null) window.Close();
    }

    private static AppServices Services(string directory)
    {
        var services = new AppServices(directory);
        services.Settings.Startup.IsAutoUpdate = false; services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Archive.SaveOnExit = false; services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.ApplySettings(); return services;
    }
    private static int Page(IDocumentWorkspace workspace) => (int)workspace.GetType().GetProperty("PageIndex").GetValue(workspace);
    private static void GoTo(IDocumentWorkspace workspace, int page) => workspace.GetType().GetMethod("GoToPage").Invoke(workspace, new object[] { page });
    private static InkStroke Stroke()
    {
        var stroke = new InkStroke(new() { BaseWidth = 3, TaperKind = TaperKind.None }); stroke.AddPoint(100, 100); stroke.AddPoint(200, 170); stroke.Complete(); return stroke;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
