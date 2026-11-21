using System.Numerics;
using InkCanvas.Features.Board;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

internal static class MediaImport
{
    public static readonly string[] Extensions = { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".3g2", ".ts", ".m2ts" };

    public static async Task<BoardMediaAsset> PickAsync(Window owner, Vector2 viewport, Matrix3x2 view, CancellationToken token)
    {
        var work = WindowChrome.GetWorkArea(owner);
        string path = await ImageFilePicker.PickAsync(new(work.X, work.Y, work.Width, work.Height), Extensions, "插入视频", "视频文件", token);
        if (path is null) return null;
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
        return await LoadAsync(file, viewport, view, token);
    }

    public static Task<BoardMediaAsset> LoadAsync(StorageFile file, Vector2 viewport, Matrix3x2 view, CancellationToken token = default)
        => Task.Run(() => LoadCoreAsync(file, viewport, view, token), token);

    private static async Task<BoardMediaAsset> LoadCoreAsync(StorageFile file, Vector2 viewport, Matrix3x2 view, CancellationToken token)
    {
        var basic = await file.GetBasicPropertiesAsync().AsTask(token);
        if (basic.Size > 512 * 1024 * 1024) throw new InvalidOperationException("视频文件超过 512 MiB。");
        if (basic.Size == 0) throw new InvalidOperationException("视频文件为空。");
        var properties = await file.Properties.GetVideoPropertiesAsync().AsTask(token);
        uint naturalWidth = properties.Width, naturalHeight = properties.Height;
        if (properties.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270)
            (naturalWidth, naturalHeight) = (naturalHeight, naturalWidth);
        if (naturalWidth == 0 || naturalHeight == 0) { naturalWidth = 16; naturalHeight = 9; }
        byte[] bytes = new byte[checked((int)basic.Size)];
        using (var input = await file.OpenStreamForReadAsync()) await input.ReadExactlyAsync(bytes, token);
        byte[] poster = await PosterAsync(file);
        token.ThrowIfCancellationRequested();
        float factor = Math.Min(viewport.X / 2 / naturalWidth, viewport.Y / 2 / naturalHeight);
        float width = Math.Max(1, naturalWidth * factor), height = Math.Max(1, naturalHeight * factor);
        return new BoardMediaAsset
        {
            Name = file.Name, Data = bytes, PosterData = poster, NaturalWidth = naturalWidth, NaturalHeight = naturalHeight,
            ContentType = ContentType(file), Width = width, Height = height,
            Left = (viewport.X - width) / 2, Top = (viewport.Y - height) / 2,
            Transform = Matrix3x2.Invert(view, out var inverse) ? BoardElementMath.Write(inverse) : null,
        };
    }

    private static async Task<byte[]> PosterAsync(StorageFile file)
    {
        try
        {
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 512);
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size == 0) return Array.Empty<byte>();
            using var stream = thumbnail.AsStreamForRead();
            var bytes = new byte[checked((int)thumbnail.Size)];
            await stream.ReadExactlyAsync(bytes);
            return bytes;
        }
        catch { return Array.Empty<byte>(); }
    }

    private static string ContentType(StorageFile file)
    {
        if (file.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return file.ContentType;
        return Path.GetExtension(file.Name).ToLowerInvariant() switch
        {
            ".avi" => "video/x-msvideo",
            ".wmv" => "video/x-ms-wmv",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".3gp" or ".3g2" => "video/3gpp",
            ".ts" or ".m2ts" => "video/mp2t",
            _ => "video/mp4",
        };
    }
}
