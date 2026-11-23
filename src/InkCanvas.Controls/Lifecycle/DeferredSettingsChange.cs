using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InkCanvas.Controls.Lifecycle;

/// <summary>Commits slider edits once after interaction, with a short debounce for keyboard changes.</summary>
public sealed class DeferredSettingsChange
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Action _commit;
    private bool _pending;

    public DeferredSettingsChange(FrameworkElement owner, Action commit, Func<bool> interacting)
    {
        _commit = commit;
        _timer = owner.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { if (!interacting()) Flush(); };
        owner.Unloaded += (_, _) => Flush();
    }

    public void Schedule()
    {
        _pending = true;
        _timer.Stop(); _timer.Start();
    }

    public void Flush()
    {
        _timer.Stop();
        if (!_pending) return;
        _pending = false;
        _commit();
    }
}
