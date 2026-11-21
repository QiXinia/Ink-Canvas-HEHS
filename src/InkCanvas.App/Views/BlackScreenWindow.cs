using InkCanvas.App.Services;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Platform.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace InkCanvas.App.Views;

/// <summary>
/// A plain black, topmost full-screen window for "look at me, not the slides". It deliberately
/// owns no ink: closing it (click or Esc) simply restores whatever was underneath. The caller
/// keeps a single instance so toggling is idempotent.
/// </summary>
public sealed class BlackScreenWindow : Window
{
    private readonly Button _focusSink;
    private bool _closing;

    public BlackScreenWindow()
    {
        Title = "黑屏";

        var root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            RequestedTheme = ElementTheme.Dark,
        };
        root.PointerPressed += (_, e) => { e.Handled = true; CloseOnce(); };

        // The window is borderless and topmost, so it can be visible without ever being active.
        // A hidden focusable control gives the window keyboard focus, which is what makes Esc
        // reach KeyDown at all.
        _focusSink = new Button
        {
            Content = new TextBlock { Text = "" },
            Width = 1,
            Height = 1,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Opacity = 0,
            IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        root.Children.Add(_focusSink);

        root.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            CloseOnce();
        };

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; CloseOnce(); };
        root.KeyboardAccelerators.Add(escape);
        Content = root;

        _focusSink.Loaded += (_, _) => FocusSink();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) FocusSink();
        };

        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        try { AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); }
        catch (Exception ex) { FileLog.WriteLogToFile($"进入黑屏全屏失败：{ex}", LogType.Error); }

        try
        {
            var work = ScreenService.WorkArea;
            WindowChrome.Place(this, work.X, work.Y, work.Width, work.Height);
        }
        catch (Exception ex) { FileLog.WriteLogToFile($"黑屏窗口定位失败：{ex}", LogType.Error); }

        Activate();
        FocusSink();
    }

    private void FocusSink()
    {
        try { _focusSink.Focus(FocusState.Programmatic); }
        catch (Exception ex) { FileLog.WriteLogToFile($"黑屏窗口获取焦点失败：{ex}", LogType.Error); }
    }

    /// <summary>Closing twice (accelerator plus KeyDown) must not throw.</summary>
    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}