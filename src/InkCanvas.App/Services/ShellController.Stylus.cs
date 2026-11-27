using InkCanvas.Host.Contracts;
using InkCanvas.Platform.Interop;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private sealed class StylusInputHost(ShellController shell) : IStylusInputHost
    {
        public string DataDirectory => Path.Combine(shell.Services.Store.Directory, "PluginData", "inkcanvas.stylus");
        public ICanvasToolsHost Canvas { get; } = new CanvasToolsHost(shell);
        public bool CanHandleShortcuts => shell.IsCanvasShortcutContext(NativeMethods.GetForegroundWindow(), allowPan: true);
        public bool CanHandlePointer(InkCanvas.Ink.InkController controller) => !shell._disposed && !shell._exiting
            && ReferenceEquals(controller, shell.ActiveController) && (!shell.DocumentActive || shell._documentWorkspace.CanEdit);
        public bool CanLearnShortcuts => shell._settings is { } settings && settings.AppWindow.IsVisible
            && NativeMethods.GetForegroundWindow() == WindowChrome.GetHwnd(settings);
    }
}
