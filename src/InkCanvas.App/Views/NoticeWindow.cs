using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

/// <summary>A dismissible notice with its own layout and native window bounds.</summary>
internal sealed class NoticeWindow : Window
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Message { get; }
    public Task Completion => _completion.Task;

    public NoticeWindow(string title, string message, int theme)
    {
        Title = title;
        Message = message;
        var body = new Grid { Padding = new Thickness(24), RowSpacing = 20, Background = PopupBrushes.WindowSurface(theme) };
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 16,
                Foreground = PopupBrushes.Foreground(theme), IsTextSelectionEnabled = true },
        });
        var close = new Button { Content = "知道了", MinWidth = 112, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(close, "知道了");
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 1);
        body.Children.Add(close);
        body.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Close(); }
        };
        Content = body;
        WindowChrome.MakeStandard(this, 520, 280, resizable: false);
        WindowFrame.Attach(this, body);
        WindowChrome.ApplyTheme(this, theme);
        body.Loaded += (_, _) => close.Focus(FocusState.Programmatic);
        Closed += (_, _) => _completion.TrySetResult();
    }
}
