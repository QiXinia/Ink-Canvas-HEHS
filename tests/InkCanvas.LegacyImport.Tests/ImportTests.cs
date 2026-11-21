using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InkCanvas.Extensions;
using Xunit;
using WpfCanvas = System.Windows.Controls.InkCanvas;

namespace InkCanvas.LegacyImport.Tests;

public sealed class ImportTests : IDisposable
{
    private const string Id = "inkcanvas.legacy-import";
    private readonly string _dir = Directory.CreateTempSubdirectory("legacy-import-test-").FullName;
    private readonly PluginManager _manager;
    public ImportTests()
    {
        _manager = new PluginManager(Path.Combine(AppContext.BaseDirectory, "PluginHost", "InkCanvas.PluginHost.exe"), _dir);
        _manager.Discover(Path.Combine(AppContext.BaseDirectory, "Packages"));
    }

    [Theory]
    [InlineData(".isf")]
    [InlineData(".icstk")]
    public async Task OpensRealIsfAfterRejectedFile(string extension)
    {
        string source = Path.Combine(_dir, "墨迹" + extension), output = Path.Combine(_dir, "out.icink");
        Assert.Empty(_manager.ImportExtensions);
        await _manager.SetEnabledAsync(Id, true);
        Assert.Contains(extension, _manager.ImportExtensions);
        File.WriteAllText(source, "invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.ConvertFileAsync(source, output));
        Assert.False(File.Exists(output));
        OnSta(() => { using var file = File.Create(source); MakeInk().Save(file); });
        await _manager.ConvertFileAsync(source, output);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(output));
        var stroke = doc.RootElement.GetProperty("Pages")[0].GetProperty("Strokes")[0];
        Assert.Equal(0xff0000ffu, stroke.GetProperty("Color").GetUInt32());
        Assert.True(stroke.GetProperty("Highlighter").GetBoolean());
        var point = stroke.GetProperty("Points")[1];
        Assert.InRange(point[0].GetDouble(), 39.95, 40.05);
        Assert.InRange(point[2].GetDouble(), .7, .8);
        await _manager.SetEnabledAsync(Id, false);
        Assert.Empty(_manager.ImportExtensions);
    }

    [Fact]
    public async Task OpensArchiveWrittenWithWpfCanvasAndOnlyUsesBundledMedia()
    {
        string path = Path.Combine(_dir, "备课.icart"), output = Path.Combine(_dir, "out.icink");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        byte[] video = { 3, 1, 4 }; // Preservation of source bytes, independent of installed video codecs.
        OnSta(() =>
        {
            string pathPng = Path.Combine(_dir, "figure one.png");
            File.WriteAllBytes(pathPng, png);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(pathPng);
            bitmap.EndInit();
            var board = new WpfCanvas { Strokes = MakeInk() };
            var image = new Image { Source = bitmap, Width = 64, Height = 32 };
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(2, 2));
            group.Children.Add(new TranslateTransform(10, 20));
            image.RenderTransform = group;
            WpfCanvas.SetLeft(image, 5);
            board.Children.Add(image);
            board.Children.Add(new MediaElement { Source = new Uri(Path.Combine(_dir, "clip.mp4")),
                LoadedBehavior = MediaState.Manual, Width = 256, Height = 256 });
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var s = archive.CreateEntry("strokes.icstk").Open()) board.Strokes.Save(s);
            using (var s = archive.CreateEntry("elements.xaml").Open()) XamlWriter.Save(board, s);
            using (var s = archive.CreateEntry("File Dependency/figure one.png").Open()) s.Write(png);
            using (var s = archive.CreateEntry("File Dependency/clip.mp4").Open()) s.Write(video);
            File.Delete(pathPng);
        });
        await _manager.SetEnabledAsync(Id, true);
        await _manager.ConvertFileAsync(path, output);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(output));
        var page = doc.RootElement.GetProperty("Pages")[0];
        Assert.Single(page.GetProperty("Strokes").EnumerateArray());
        var image = page.GetProperty("Images")[0];
        Assert.Equal(png, image.GetProperty("Data").GetBytesFromBase64());
        Assert.Equal(5, image.GetProperty("Left").GetDouble());
        Assert.Equal(new double[] { 2, 0, 0, 2, 5, 20 },
            image.GetProperty("Transform").EnumerateArray().Select(x => x.GetDouble()).ToArray());
        Assert.Equal(video, page.GetProperty("Media")[0].GetProperty("Data").GetBytesFromBase64());

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            archive.GetEntry("File Dependency/clip.mp4")!.Delete();
        string failed = Path.Combine(_dir, "missing.icink");
        await Assert.ThrowsAsync<InvalidDataException>(() => _manager.ConvertFileAsync(path, failed));
        Assert.False(File.Exists(failed));
    }

    private static StrokeCollection MakeInk()
    {
        var points = new StylusPointCollection { new StylusPoint(1, 2, .25f), new StylusPoint(40, 50, .75f) };
        return new StrokeCollection { new Stroke(points) { DrawingAttributes = new DrawingAttributes
            { Color = Colors.Blue, Width = 8, IsHighlighter = true } } };
    }

    private static void OnSta(Action action)
    {
        Exception error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public void Dispose() { _manager.Dispose(); Directory.Delete(_dir, true); }
}
