using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace InkCanvas.Controls.Controls;

public static class ButtonHoldAction
{
    /// <summary>Invokes the hold action for a stationary primary pointer and consumes its eventual click.</summary>
    public static void Attach(Button button, Action clicked, Action held)
    {
        var timer = button.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(550);
        timer.IsRepeating = false;
        uint? pointer = null;
        Windows.Foundation.Point start = default;
        bool fired = false;
        timer.Tick += (_, _) =>
        {
            if (pointer is null || !button.IsEnabled || !button.IsLoaded) return;
            fired = true;
            held();
        };
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(button);
            if (pointer is not null || (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
                ? !point.Properties.IsLeftButtonPressed : !point.IsInContact)) return;
            fired = false;
            pointer = args.Pointer.PointerId;
            start = point.Position;
            timer.Start();
        }), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (pointer != args.Pointer.PointerId || fired) return;
            var point = args.GetCurrentPoint(button).Position;
            if ((point.X - start.X) * (point.X - start.X) + (point.Y - start.Y) * (point.Y - start.Y) > 144)
                Stop();
        }), true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(End), true);
        button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(End), true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(End), true);
        button.Unloaded += (_, _) => Stop();
        button.Click += (_, _) => { if (!fired) clicked(); };

        void End(object sender, PointerRoutedEventArgs args)
        {
            if (pointer != args.Pointer.PointerId) return;
            Stop();
            if (fired) button.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => fired = false);
        }

        void Stop() { pointer = null; timer.Stop(); }
    }
}
