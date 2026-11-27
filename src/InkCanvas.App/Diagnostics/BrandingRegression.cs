using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InkCanvas.App.Diagnostics;

internal static class BrandingRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var body = new Grid { RequestedTheme = ElementTheme.Light };
        var logo = new Image { Width = 160, Height = 160, HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(logo);
        var window = new Window { Title = "Ink Canvas 标志主题检查", Content = body };
        try
        {
            WindowChrome.MakeStandard(window, 540, 380, topmost: false);
            var frame = WindowFrame.Attach(window, body);
            window.Activate();
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                body.RequestedTheme = theme;
                string name = theme.ToString();
                logo.Source = new BitmapImage(new Uri($"ms-appx:///Assets/InkCanvas.{name}.png"));
                await Task.Delay(200);
                var image = (frame.Header.IconSource as ImageIconSource)?.ImageSource as BitmapImage;
                Check("title bar logo follows actual theme", image?.UriSource.AbsoluteUri.EndsWith($"InkCanvas.{name}.png") == true);
                Check("native window icon is set", SendMessage(WindowChrome.GetHwnd(window), 0x007F, 0, 0) != IntPtr.Zero);
                await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, $"logo-{name}.png"));
            }
        }
        catch (Exception exception) { Check("branding diagnostic exception", false, exception.ToString()); }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(directory, "branding.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed, string detail = null) => checks.Add(new { check = name, passed, detail });
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, IntPtr data);
}
