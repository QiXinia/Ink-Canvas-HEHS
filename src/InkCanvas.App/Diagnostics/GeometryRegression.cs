using System.Reflection;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

internal static class GeometryRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var services = new AppServices(Path.Combine(directory, "settings")); services.Settings.Startup.IsAutoUpdate = false;
        using var shell = new ShellController(services); shell.Start(); shell.ToggleBoard();
        try
        {
            await Task.Delay(300);
            var service = services.Modules.GetExport<IAnalyticGeometryService>() ?? throw new InvalidOperationException("Geometry module not available " + JsonSerializer.Serialize(services.Modules.Status));
            var host = shell.CanvasToolsTestHost;
            var type = service.GetType().Assembly.GetType("InkCanvas.Modules.AnalyticGeometry.GeometryDiagnostics", true)!;
            await (Task)type.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { host, directory });
            var source = services.Board.Current.Document.Strokes.FirstOrDefault(stroke => stroke.ShapeSource is not null)?.ShapeSource;
            if (source is null) throw new InvalidOperationException("Geometry insert did not reach current board page.");
            services.Board.SaveBoard(Path.Combine(directory, "geometry-board.icink"));
            var target = host.CaptureTarget(); services.Board.InsertPageAfterCurrent();
            bool rejected = !host.CommitShape(target, target.Controller.Document.Strokes);
            var report = new { stalePageRejected = rejected, boardMenuHasGeometryCommand = shell.ExtensionCommands.Any(command => command.PluginId == "inkcanvas.analytic-geometry"), version = typeof(App).Assembly.GetName().Version.ToString() };
            await File.WriteAllTextAsync(Path.Combine(directory, "geometry-integration.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(directory, "geometry-error.txt"), ex.ToString()); }
        finally { foreach (var board in shell.OpenWindows.OfType<BoardWindow>().ToArray()) board.Close(); shell.Dispose(); Application.Current.Exit(); }
    }
}
