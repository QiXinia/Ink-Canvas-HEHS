using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Views;

internal sealed class NamesInputWindow : Window
{
    private readonly ClassroomRosterEditor _editor;
    private bool _allowClose, _confirming;
    internal string RosterText { get => _editor.RosterText; set => _editor.RosterText = value; }

    public NamesInputWindow(int theme, string dataDirectory = null)
    {
        Title = "课堂名单 - Ink Canvas";
        _editor = new ClassroomRosterEditor(this, dataDirectory ?? LotteryWindow.DataDir)
            { Padding = new Thickness(24), Background = PopupBrushes.WindowSurface(theme) };
        Content = _editor;
        WindowChrome.MakeStandard(this, 680, 720);
        WindowFrame.Attach(this, _editor);
        WindowChrome.ApplyTheme(this, theme);
        var minimum = new MinSizeHook(this, 540, 440);
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { _editor.Close(); minimum.Dispose(); };
    }

    internal Task<bool> SaveAsync() => _editor.SaveAsync();
    internal void CloseWithoutSaving() { _allowClose = true; Close(); }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !_editor.HasChanges && !_editor.IsBusy) return;
        args.Cancel = true;
        if (_confirming || _editor.IsBusy) return;
        _confirming = true;
        try { if (await _editor.ConfirmCloseAsync()) CloseWithoutSaving(); }
        finally { _confirming = false; }
    }
}
