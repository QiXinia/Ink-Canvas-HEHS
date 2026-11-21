using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;

namespace InkCanvas.Modules.ScreenTracking;

public sealed class ScreenTrackingModule : IApplicationModule
{
    private ScreenAnnotationCoordinator _coordinator;
    public void Start(IModuleContext context)
    {
        _coordinator = new ScreenAnnotationCoordinator(context.GetService<IScreenTrackingHost>(), context.Protect);
        context.RegisterCleanup(_coordinator.Dispose);
        context.Export<IScreenTrackingService>(_coordinator);
    }
    public void Stop() { _coordinator?.Dispose(); _coordinator = null; }
}
