using InkCanvas.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>Nonactivating, short-lived continuation prompt with one-shot cleanup.</summary>
internal sealed class ResumeReminderWindow : Window
{
    private readonly DispatcherQueueTimer _timer;
    private bool _accepted, _finished;
    internal ResumeReminderWindow(string title, string description, int theme, Func<Task> resume, Func<Task> dismiss)
    {
        Title = title;
        var foreground = new SolidColorBrush(theme == 2 ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        var continueButton = new Button { Content = "继续", MinWidth = 88, MinHeight = 44,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var skip = new Button { Content = "忽略", MinWidth = 80, MinHeight = 44 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { skip, continueButton } };
        Content = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16),
            Background = new SolidColorBrush(theme == 2 ? Windows.UI.Color.FromArgb(250, 32, 32, 32) : Windows.UI.Color.FromArgb(250, 250, 250, 250)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 128, 128, 128)), BorderThickness = new Thickness(1),
            Child = new StackPanel { Width = 340, Spacing = 10, Children = {
                new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = foreground },
                new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = foreground }, actions } } };
        WindowChrome.MakeBorderless(this, topmost: true, noActivate: true);
        WindowChrome.ApplyTheme(this, theme);
        WindowChrome.ClipToSurfaces(this, () => new[] { (Border)Content });
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(8); _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Close();
        skip.Click += (_, _) => Close();
        continueButton.Click += async (_, _) =>
        {
            if (_finished) return;
            _accepted = true; Close();
            await resume();
        };
        Closed += async (_, _) => { _finished = true; _timer.Stop(); if (!_accepted) await dismiss(); };
    }

    internal void Show(int stackIndex)
    {
        var body = (FrameworkElement)Content;
        body.Measure(new Windows.Foundation.Size(380, double.PositiveInfinity));
        var screen = InkCanvas.Platform.System.ScreenService.AtCursor().WorkArea;
        double dpi = WindowChrome.GetDpiScale(this);
        int width = (int)Math.Ceiling(body.DesiredSize.Width * dpi), height = (int)Math.Ceiling(body.DesiredSize.Height * dpi);
        int x = Math.Clamp(screen.X + screen.Width - width - 20, screen.X, screen.X + Math.Max(0, screen.Width - width));
        int y = Math.Clamp(screen.Y + screen.Height - height - 20 - stackIndex * (height + 12), screen.Y, screen.Y + Math.Max(0, screen.Height - height));
        WindowChrome.PlaceClient(this, x, y, width, height);
        WindowChrome.ShowWithoutActivation(this); _timer.Start();
    }
}
