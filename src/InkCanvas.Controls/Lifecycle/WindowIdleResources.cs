using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace InkCanvas.Controls.Lifecycle;

/// <summary>Retains warm resources briefly when hidden, then rebuilds them on the next show.</summary>
public sealed class WindowIdleResources : IDisposable
{
    private readonly Window _window;
    private readonly Action _release, _restore;
    private readonly DispatcherQueueTimer _timer;
    private bool _active, _disposed;

    public WindowIdleResources(Window window, Action release, Action restore, TimeSpan? delay = null)
    {
        _window = window;
        _release = release;
        _restore = restore;
        _timer = window.DispatcherQueue.CreateTimer();
        _timer.Interval = delay ?? TimeSpan.FromSeconds(15);
        _timer.IsRepeating = false;
        _timer.Tick += OnIdle;
        window.AppWindow.Changed += OnWindowChanged;
        window.Activated += OnActivated;
        window.Closed += OnClosed;
        _active = IsActive;
        if (!_active) _timer.Start();
    }

    public bool IsActive => !_disposed && _window.AppWindow.IsVisible
        && _window.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
    public bool IsReleased { get; private set; }

    private void Update()
    {
        if (_disposed) return;
        bool active = IsActive;
        if (active == _active) return;
        _active = active;
        if (active)
        {
            _timer.Stop();
            IsReleased = false;
            _restore();
        }
        else _timer.Start();
    }

    /// <summary>Release only when hidden or minimized. Does not modify the document or undo history.</summary>
    public bool ReleaseNow()
    {
        if (_disposed || IsActive || IsReleased) return false;
        _timer.Stop();
        IsReleased = true;
        _release();
        return true;
    }

    private void OnIdle(DispatcherQueueTimer sender, object args) => ReleaseNow();
    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => Update();
    private void OnActivated(object sender, WindowActivatedEventArgs args) => Update();
    private void OnClosed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnIdle;
        _window.AppWindow.Changed -= OnWindowChanged;
        _window.Activated -= OnActivated;
        _window.Closed -= OnClosed;
    }
}
