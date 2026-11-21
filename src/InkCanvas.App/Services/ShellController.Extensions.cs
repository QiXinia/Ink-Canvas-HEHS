using System.Numerics;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Extensibility;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private IReadOnlyList<ExtensionCommand> _displayedExtensionCommands = Array.Empty<ExtensionCommand>();
    public IReadOnlyList<ExtensionCommand> ExtensionCommands
    {
        get
        {
            var commands = Services.Plugins.Commands.ToList();
            if (Services.Modules.HasExport<Host.Contracts.IPdfWorkspaceService>()) commands.AddRange(new[] {
            new ExtensionCommand("inkcanvas.pdf", "open", "打开 PDF", "查看 PDF 并将墨迹批注保存到文件"),
            new ExtensionCommand("inkcanvas.pdf", "import", "PDF 导入画板", "选择 PDF 页码并导入新画板页") });
            if (Services.Modules.HasExport<Host.Contracts.IAnalyticGeometryService>()) commands.Add(new ExtensionCommand("inkcanvas.analytic-geometry", "open", "解析几何", "数学工作区、代数计算、二维与三维联动作图"));
            return commands;
        }
    }

    private async Task StartPluginsAsync()
    {
        try { await Services.Plugins.StartEnabledAsync(new HashSet<string>(Services.ExtensionPreferences.EnabledPlugins)); }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("Plugin startup: " + ex.Message, Core.Diagnostics.LogType.Error); }
    }
    private void OnExtensionCommandsChanged()
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            var commands = ExtensionCommands.ToArray();
            if (_displayedExtensionCommands.SequenceEqual(commands)) return;
            _displayedExtensionCommands = commands;
            _bar?.ClosePanels(); _bar?.Bar.RefreshExtensionCommands();
            _board?.RefreshExtensionCommands();
        });
    }
    public async Task ExecuteExtensionCommandAsync(ExtensionCommand command)
    {
        // A menu or detached panel may outlive a module that stopped after it opened.
        if (!ExtensionCommands.Any(available => available.PluginId == command.PluginId && available.CommandId == command.CommandId))
        { OnExtensionCommandsChanged(); return; }
        if (command.PluginId == "inkcanvas.analytic-geometry")
        {
            try { Services.Modules.GetExport<Host.Contracts.IAnalyticGeometryService>()?.Open(); }
            catch (Exception ex) { await NotifyAsync("解析几何", ex.GetBaseException().Message); }
            return;
        }
        if (command.PluginId == "inkcanvas.pdf")
        {
            if (command.CommandId == "open") await OpenPdfAsync();
            else if (command.CommandId == "import")
            {
                try { var pdf = Services.Modules.GetExport<Host.Contracts.IPdfWorkspaceService>(); if (pdf is not null) await pdf.ImportToBoardAsync(); }
                catch (Exception ex) { await NotifyAsync("PDF 导入失败", ex.GetBaseException().Message); }
            }
            return;
        }
        var controller = ActiveController;
        if (controller.HasActiveInteraction || controller.Playback is not null) return;
        if (!Matrix3x2.Invert(controller.ViewTransform, out var inverse)) return;
        var originalView = controller.ViewTransform;
        bool canvasChanged = false;
        void InvalidateCanvas() => canvasChanged = true;
        controller.Document.Changed += InvalidateCanvas;
        controller.ElementsChanged += InvalidateCanvas;
        controller.PlaybackChanged += InvalidateCanvas;
        var context = new PluginCommandContext(controller.ViewCenter.X * 2, controller.ViewCenter.Y * 2, _board is null ? "screen" : "board");
        try
        {
            var result = await Services.Plugins.ExecuteAsync(command.PluginId, command.CommandId, context);
            if (_disposed || ActiveController != controller || canvasChanged || controller.ViewTransform != originalView
                || controller.HasActiveInteraction || controller.Playback is not null) return;
            var strokes = new List<InkStroke>();
            long layer = controller.NextLayerOrder();
            foreach (var item in result.Strokes)
            {
                var stroke = new InkStroke(new StrokeAttributes { BaseWidth = item.Width, Color = InkColor.FromPackedArgb(item.Argb), TaperKind = TaperKind.None }) { LayerOrder = layer++ };
                foreach (var point in item.Points) stroke.AddPoint(point.X, point.Y);
                stroke.Complete(); stroke.Transform(inverse); strokes.Add(stroke);
            }
            if (strokes.Count > 0)
            {
                controller.Document.AddRange(strokes);
                controller.History.Push(new DelegateAction(command.Title, () => controller.Document.AddRange(strokes), () => controller.Document.RemoveAll(strokes)));
            }
            if (!string.IsNullOrWhiteSpace(result.Notification)) await NotifyAsync(command.Title, result.Notification);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile("Plugin command " + command.PluginId + "/" + command.CommandId + ": " + ex.Message, Core.Diagnostics.LogType.Error);
            if (!_disposed) await NotifyAsync("插件命令执行失败", ex.GetBaseException().Message);
        }
        finally
        {
            controller.Document.Changed -= InvalidateCanvas;
            controller.ElementsChanged -= InvalidateCanvas;
            controller.PlaybackChanged -= InvalidateCanvas;
        }
    }
}
