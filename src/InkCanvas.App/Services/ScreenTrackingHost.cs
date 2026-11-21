using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;

namespace InkCanvas.App.Services;

internal sealed class ScreenTrackingHost(AppServices services, OverlayHost overlay, Func<bool> boardOpen) : IScreenTrackingHost
{
    public InkController Controller => services.OverlayInk;
    public BoardService Images => services.ScreenImages;
    public LayeredInkWindow Window => overlay.Window;
    public OverlayInkPresenter Presenter => overlay.Presenter;
    public ExperimentalSettings Settings => services.Settings.Experimental;
    public bool BoardOpen => boardOpen();
    public string Status { get => services.ScreenTrackingStatus; set => services.ScreenTrackingStatus = value; }
    public event Action SettingsApplied { add => services.SettingsApplied += value; remove => services.SettingsApplied -= value; }
    public event Action ResetRequested { add => services.ScreenTrackingResetRequested += value; remove => services.ScreenTrackingResetRequested -= value; }
}
