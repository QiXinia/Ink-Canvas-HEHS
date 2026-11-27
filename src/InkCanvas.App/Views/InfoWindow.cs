using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>Release notes with left navigation and a shared WinUI title bar.</summary>
public sealed class InfoWindow : Window
{
    private bool _entranceShown;
    private MinSizeHook _minSize;

    /// <summary>Release notes displayed in a scrollable workspace.</summary>
    public InfoWindow(string title, string markdown, int theme)
    {
        Title = title;

        var body = new MarkdownView(theme) { Padding = new Thickness(24) };
        string version = typeof(InfoWindow).Assembly.GetName().Version?.ToString(3) ?? "3.7.3";
        body.SetMarkdown(markdown, new Uri("https://github.com/QiXinia/Ink-Canvas-HEHS/blob/v" + version + "/"));

        var workspace = new WindowWorkspace { IsTitleBarAutoPaddingEnabled = false };
        workspace.AddPage("版本更新", "", () => body);
        workspace.SelectPage("版本更新");
        FinishChrome(workspace, theme, 980, 680);
    }

    private void FinishChrome(FrameworkElement content, int theme, int widthDip, int heightDip)
    {
        var root = new Grid { Background = PopupBrushes.WindowSurface(theme) };
        root.Children.Add(content);
        Content = root;
        WindowChrome.MakeStandard(this, widthDip, heightDip);
        WindowFrame.Attach(this, root);
        _minSize = new MinSizeHook(this, 540, 360);
        WindowChrome.ApplyTheme(this, theme);
        root.ActualThemeChanged += (_, _) =>
        {
            int resolved = root.ActualTheme == ElementTheme.Dark ? 2 : 1;
            root.Background = PopupBrushes.WindowSurface(resolved);
        };
        root.Loaded += (_, _) =>
        {
            if (_entranceShown) return;
            _entranceShown = true;
            Motion.PlayEntrance(content, 10f);
        };
        Closed += (_, _) => { _minSize?.Dispose(); _minSize = null; };
    }

    public static InfoWindow OpenChangeLog(int theme)
    {
        string text;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            text = File.Exists(path) ? File.ReadAllText(path) : "未找到更新日志文件。";
        }
        catch
        {
            text = "无法读取更新日志。";
        }
        return new InfoWindow("版本更新 - Ink Canvas Artistry", text, theme);
    }
}
