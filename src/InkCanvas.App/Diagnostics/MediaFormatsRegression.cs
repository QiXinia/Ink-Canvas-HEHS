using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.History;
using InkCanvas.Core.Imaging;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace InkCanvas.App.Diagnostics;

internal static class MediaFormatsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            var fixtures = Path.GetFullPath(Path.Combine(directory, "..", "media-format-fixtures"));
            await CreateJpegXrFixture(fixtures);
            await File.WriteAllTextAsync(Path.Combine(fixtures, "sample-namespace-free.svg"),
                "<svg viewBox='0 0 320 180'><rect width='320' height='180' fill='#147d64'/></svg>");
            var extensions = ImageImport.SupportedExtensions;
            checks.Add(new { check = "registered image formats", extensions });
            Check("picker includes native vector and additional raster formats", new[] { ".svg", ".svgz", ".webp", ".ico", ".jxr", ".wdp", ".hdp" }
                .All(extensions.Contains));
            foreach (string path in Directory.GetFiles(fixtures).Where(path => !Path.GetFileName(path).StartsWith("invalid")))
            {
                string extension = Path.GetExtension(path);
                if (!extensions.Contains(extension)) continue;
                var file = await StorageFile.GetFileFromPathAsync(path);
                try
                {
                    var asset = await ImageImport.LoadAsync(file, true, new(1000, 700), Matrix3x2.CreateScale(1.25f));
                    using var controller = new InkController();
                    var board = new BoardService(controller); board.AddImage(asset);
                    using var renderer = new InkSceneRenderer { ShowSelectionOutline = false };
                    await renderer.Images.PrepareAsync(device, controller);
                    using var target = new CanvasRenderTarget(device, 1000, 700, 96);
                    using (var draw = target.CreateDrawingSession())
                    {
                        draw.Clear(Microsoft.UI.Colors.Transparent);
                        renderer.DrawCommitted(draw, controller);
                    }
                    var pixels = target.GetPixelBytes();
                    bool svg = extension is ".svg" or ".svgz";
                    Check("format imports and renders " + file.Name, pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 200)
                        && (!svg || asset.ContentType == SvgImage.ContentType && renderer.Images.CachedVectorCount == 1));
                    if (file.Name is "sample.webp" or "sample.jxr" or "sample.wdp" or "sample.hdp")
                        Check(file.Name + " normalizes into a portable bitmap", asset.ContentType == "image/png"
                            && Math.Abs(asset.Width / asset.Height - 16f / 9) < .01f);
                    if (file.Name == "rotated.jpg") Check("photo orientation preserves displayed aspect ratio", Math.Abs(asset.Width / asset.Height - 180f / 320) < .001f);
                    await target.SaveAsync(Path.Combine(directory, file.Name + ".png"), CanvasBitmapFileFormat.Png);
                    if (svg)
                    {
                        var element = (BoardSelectionElement)board.GetSelectionElements().Single();
                        controller.SelectElement(element);
                        controller.SetSelectionImageOpacity(.5f);
                        controller.TransformSelectionUndoable(Matrix3x2.CreateScale(2, controller.SelectionBounds.Center), "scale vector");
                        using var zoomed = new CanvasRenderTarget(device, 1600, 1000, 96);
                        using (var draw = zoomed.CreateDrawingSession())
                        {
                            draw.Clear(Microsoft.UI.Colors.Transparent);
                            renderer.DrawCommitted(draw, controller);
                        }
                        Check("vector opacity applies once " + file.Name, zoomed.GetPixelBytes().Where((_, index) => index % 4 == 3).Max() is >= 126 and <= 129);
                        var duplicate = (BoardSelectionElement)element.Duplicate(); duplicate.SetPresent(true);
                        await renderer.Images.PrepareAsync(device, controller);
                        Check("vector clone shares native document " + file.Name, renderer.Images.CachedVectorCount == 1
                            && ReferenceEquals(duplicate.Image.Data, asset.Data));
                        string saved = Path.Combine(directory, file.Name + ".icink"); board.SavePage(saved);
                        var loaded = new BoardService(); loaded.LoadPage(saved);
                        Check("vector persistence retains source " + file.Name, loaded.CurrentImages.All(image => image.ContentType == SvgImage.ContentType
                            && image.Data.SequenceEqual(asset.Data)));
                        await zoomed.SaveAsync(Path.Combine(directory, file.Name + "-zoomed.png"), CanvasBitmapFileFormat.Png);
                    }
                }
                catch (InvalidOperationException ex) when (extension is ".avif" or ".heif" or ".heic"
                    && ex.InnerException is COMException)
                {
                    checks.Add(new { check = "optional system codec " + file.Name, available = false, message = ex.Message });
                    Check("unavailable system codec provides an actionable message", ex.Message.Contains("图像扩展"));
                }
                catch (Exception ex) { checks.Add(new { check = "format exception " + file.Name, passed = false, error = ex.ToString() }); }
            }
            try
            {
                await ImageImport.LoadAsync(await StorageFile.GetFileFromPathAsync(Path.Combine(fixtures, "invalid.svg")), true, new(1000, 700), Matrix3x2.Identity);
                Check("invalid vector fails before insertion", false);
            }
            catch (InvalidDataException) { Check("invalid vector fails before insertion", true); }

            var composition = new MediaComposition();
            composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(await StorageFile.GetFileFromPathAsync(Path.Combine(fixtures, "sample.png")), TimeSpan.FromSeconds(3)));
            var output = await (await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(directory))).CreateFileAsync("sample.mp4", CreationCollisionOption.ReplaceExisting);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Wvga);
            profile.Video.Width = 640; profile.Video.Height = 360;
            var rendered = await composition.RenderToFileAsync(output, MediaTrimmingPreference.Precise, profile);
            Check("test video encodes with system media pipeline", rendered == TranscodeFailureReason.None);
            if (rendered != TranscodeFailureReason.None) return;
            shell.ToggleBoard(); await Task.Delay(180);
            var window = shell.OpenWindows.OfType<BoardWindow>().Single();
            var video = await MediaImport.LoadAsync(output, new(1200, 800), Matrix3x2.Identity);
            Check("video placement follows natural aspect ratio", Math.Abs(video.Width / video.Height - 16f / 9) < .01f
                && video.NaturalWidth == 640 && video.NaturalHeight == 360);
            window.InsertMedia(video);
            var mediaLayer = window.MediaLayer;
            await WaitUntil(() => !mediaLayer.IsLoading(video), 10000);
            var player = mediaLayer.GetPlayer(video);
            Check("video opens from shared embedded bytes", player is not null && player.PlaybackSession.NaturalVideoWidth == 640);
            Check("inserted video is selected", window.CurrentController.SelectedElements.Single().Id == video.Id);
            using (var memory = video.OpenRead()) Check("playback stream wraps existing bytes", ReferenceEquals(memory.GetBuffer(), video.Data) && !memory.CanWrite);
            var view = (Grid)mediaLayer.GetViewElement(video);
            var elementView = view.Children.OfType<MediaPlayerElement>().Single();
            Check("video display is clipped with compact controls", view.Clip is RectangleGeometry && elementView.MinWidth == 0
                && elementView.TransportControls.IsCompact);
            player.Play();
            await Task.Delay(300);
            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "video-in-board.png"));
            var position = player.PlaybackSession.Position;
            for (int index = 0; index < 60; index++)
            {
                video.Transform = BoardElementMath.Write(Matrix3x2.CreateTranslation(index / 2f, index / 4f));
                services.Board.NotifyMediaPlacement(video);
                services.Board.Current.Document.Touch();
            }
            Check("geometry and layer refresh keep the same playing decoder", ReferenceEquals(player, mediaLayer.GetPlayer(video))
                && player.PlaybackSession.Position >= position && mediaLayer.PlayerCount == 1);
            player.Pause();
            player.PlaybackSession.Position = TimeSpan.FromSeconds(1);
            var selected = window.CurrentController.SelectedElements.Single();
            var copy = (BoardSelectionElement)selected.Duplicate(); copy.SetPresent(true);
            await WaitUntil(() => !mediaLayer.IsLoading(copy.Media), 5000);
            Check("video clone shares bytes and poster", ReferenceEquals(video.Data, copy.Media.Data)
                && ReferenceEquals(video.PosterData, copy.Media.PosterData) && mediaLayer.PlayerCount == 2);
            copy.SetPresent(false);
            Check("deleting clone releases its decoder", mediaLayer.PlayerCount == 1);
            var broken = new BoardMediaAsset { Name = "broken", Data = new byte[] { 1, 2, 3 }, Width = 200, Height = 120 };
            services.Board.AddMedia(broken);
            await WaitUntil(() => !mediaLayer.IsLoading(broken), 8000);
            Check("corrupt video shows a retry state", mediaLayer.GetStatus(broken)?.StartsWith("无法播放") == true);
            var retry = Descendants(mediaLayer.GetViewElement(broken)).OfType<InkCanvas.Controls.Controls.TouchIconButton>().Single(button => button.Label == "重试");
            ((IInvokeProvider)new ButtonAutomationPeer(retry).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUntil(() => !mediaLayer.IsLoading(broken), 8000);
            Check("retry retains one player per video", mediaLayer.PlayerCount == 2);
            services.Board.RemoveMedia(broken);
            var controlsButton = Descendants(elementView).OfType<Button>().FirstOrDefault();
            if (controlsButton is not null) Check("selection routing recognizes video controls", BoardMediaLayer.IsPlaybackControl(controlsButton));
            services.Board.NextPage();
            Check("page change releases previous video players", mediaLayer.PlayerCount == 0);
            services.Board.PreviousPage();
            await WaitUntil(() => !mediaLayer.IsLoading(video), 5000);
            Check("returning to page restores embedded video", mediaLayer.PlayerCount == 1);
            shell.ToggleBoard();
            Check("board closes cleanly after video playback", !shell.OpenWindows.OfType<BoardWindow>().Any());
        }
        catch (Exception ex) { checks.Add(new { check = "media formats exception", passed = false, error = ex.ToString() }); }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "media-formats.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static async Task WaitUntil(Func<bool> completed, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!completed() && timer.ElapsedMilliseconds < milliseconds) await Task.Delay(50);
    }

    private static async Task CreateJpegXrFixture(string fixtures)
    {
        var source = await StorageFile.GetFileFromPathAsync(Path.Combine(fixtures, "sample.png"));
        using var input = await source.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(fixtures);
        var file = await folder.CreateFileAsync("sample.jxr", CreationCollisionOption.ReplaceExisting);
        using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegXREncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
        }
        await file.CopyAsync(folder, "sample.wdp", NameCollisionOption.ReplaceExisting);
        await file.CopyAsync(folder, "sample.hdp", NameCollisionOption.ReplaceExisting);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
