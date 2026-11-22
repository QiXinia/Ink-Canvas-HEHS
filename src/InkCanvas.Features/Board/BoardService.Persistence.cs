using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Persistence;

namespace InkCanvas.Features.Board;

public sealed partial class BoardService
{
    private sealed record BoardSnapshot(List<PreparedPage> Pages, BoardArchiveMetadata Metadata, Dictionary<string, byte[]> Extensions);
    private sealed record BoardBlobReference(int Page, int Index, string Kind, string Entry, long Length);
    private static readonly JsonSerializerOptions PackageJsonOptions = CreatePackageJsonOptions();
    private static readonly SemaphoreSlim PackageCommitGate = new(1, 1);
    private Task _saveQueue = Task.CompletedTask;
    public BoardArchiveMetadata ArchiveMetadata { get; private set; }

    private static JsonSerializerOptions CreatePackageJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(BoardImageAsset) && type.Type != typeof(BoardMediaAsset)) return;
            foreach (var property in type.Properties)
                if (property.Name is nameof(BoardImageAsset.Data) or nameof(BoardMediaAsset.PosterData))
                {
                    property.ShouldSerialize = (_, _) => false;
                    property.Set = null;
                }
        });
        return new JsonSerializerOptions(JsonOptions) { WriteIndented = false, TypeInfoResolver = resolver };
    }

    // Synchronous entry points remain available for existing integrations. UI callers use async APIs.
    public void SavePage(string path) => SavePageAsync(path).GetAwaiter().GetResult();
    public void SaveBoard(string path) => SaveBoardAsync(path).GetAwaiter().GetResult();
    public void LoadPage(string path) => PrepareOpenAsync(path, false).GetAwaiter().GetResult()();
    public void LoadBoard(string path) => PrepareOpenAsync(path, true).GetAwaiter().GetResult()();

    public Task SavePageAsync(string path, CancellationToken cancellationToken = default) => SavePageArchiveAsync(path, null, cancellationToken: cancellationToken);

    public Task SavePageArchiveAsync(string path, BoardArchiveMetadata metadata, bool overwrite = true, CancellationToken cancellationToken = default) =>
        QueueSave(path, CaptureSnapshot(new[] { CurrentIndex }, metadata), overwrite, cancellationToken);

    public Task SaveBoardAsync(string path, CancellationToken cancellationToken = default) => SaveBoardArchiveAsync(path, null, cancellationToken: cancellationToken);

    public Task SaveBoardArchiveAsync(string path, BoardArchiveMetadata metadata, bool overwrite = true, CancellationToken cancellationToken = default) =>
        QueueSave(path, CaptureSnapshot(Enumerable.Range(0, PageCount), metadata), overwrite, cancellationToken);

    private Task QueueSave(string path, BoardSnapshot snapshot, bool overwrite, CancellationToken cancellationToken)
    {
        var previous = _saveQueue;
        return _saveQueue = SaveAfterPreviousAsync();

        async Task SaveAfterPreviousAsync()
        {
            // Keep request order so an older large snapshot cannot overwrite a newer small one.
            try { await previous.ConfigureAwait(false); }
            catch (Exception) { }
            await SaveSnapshotAsync(path, snapshot, overwrite, cancellationToken).ConfigureAwait(false);
        }
    }

    private BoardSnapshot CaptureSnapshot(IEnumerable<int> indices, BoardArchiveMetadata metadata)
    {
        FlushExtensions();
        return new(
        indices.Select(index => new PreparedPage(
            _pages[index].Document.Strokes.Select(stroke => stroke.Clone()).ToList(),
            _images[index].Select(image => image.Copy(copyData: false)).ToList(),
            _media[index].Select(media => media.Copy(copyData: false)).ToList(), _papers[_pages[index]], _pages[index].ViewTransform)
            { Viewport = new[] { _pages[index].ViewCenter.X * 2, _pages[index].ViewCenter.Y * 2 } }).ToList(), metadata ?? ArchiveMetadata,
        new Dictionary<string, byte[]>(_extensions, StringComparer.Ordinal));
    }

    private static Task SaveSnapshotAsync(string path, BoardSnapshot snapshot, bool overwrite, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var model = new BoardFile
            {
                Format = BoardFormat, Version = BoardFormatVersion,
                Pages = snapshot.Pages.Select(page => new BoardPageFile
                {
                    Background = page.Paper.Background.ToString(), GridSpacing = page.Paper.GridSpacing,
                    Name = page.Paper.Name,
                    ViewTransform = new[] { page.View.M11, page.View.M12, page.View.M21, page.View.M22, page.View.M31, page.View.M32 },
                    Viewport = page.Viewport,
                    Strokes = page.Strokes.Select(ToStrokeFile).ToList(), Images = page.Images, Media = page.Media,
                }).ToList(),
            };
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // Covers are optional derived data. A device-loss must never prevent saving editable content.
                    try
                    {
                        var previewBoard = new BoardService();
                        try
                        {
                            previewBoard.ApplyBoard(new[] { snapshot.Pages[0] });
                            byte[] cover = await BoardPreview.RenderAsync(previewBoard, token: cancellationToken).ConfigureAwait(false);
                            var coverEntry = archive.CreateEntry(BoardPreview.CoverEntry, CompressionLevel.NoCompression);
                            await using (var content = coverEntry.Open()) await content.WriteAsync(cover, cancellationToken).ConfigureAwait(false);
                            var previewInfo = archive.CreateEntry("preview/info.json", CompressionLevel.Fastest);
                            await using var info = previewInfo.Open();
                            await JsonSerializer.SerializeAsync(info, new BoardPreviewInfo(1, snapshot.Pages.Count, snapshot.Metadata?.Title), cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        finally { foreach (var page in previewBoard.Pages) page.Dispose(); }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { Core.Diagnostics.FileLog.WriteLogToFile("白板封面生成失败 " + ex.Message); }
                    var blobs = new Dictionary<byte[], string>(ReferenceEqualityComparer.Instance);
                    for (int pageIndex = 0; pageIndex < model.Pages.Count; pageIndex++)
                    {
                        var page = model.Pages[pageIndex];
                        for (int index = 0; index < page.Images.Count; index++)
                            await WriteBlob(pageIndex, index, "image", page.Images[index].Data);
                        for (int index = 0; index < page.Media.Count; index++)
                        {
                            await WriteBlob(pageIndex, index, "video", page.Media[index].Data);
                            await WriteBlob(pageIndex, index, "poster", page.Media[index].PosterData);
                        }
                    }
                    foreach (var extension in snapshot.Extensions)
                    {
                        var entry = archive.CreateEntry("extensions/" + extension.Key + ".bin", CompressionLevel.Optimal);
                        await using var content = entry.Open();
                        await content.WriteAsync(extension.Value, cancellationToken).ConfigureAwait(false);
                    }
                    var manifest = archive.CreateEntry("board.json", CompressionLevel.Fastest);
                    await using (var metadata = manifest.Open())
                        await JsonSerializer.SerializeAsync(metadata, model, PackageJsonOptions, cancellationToken).ConfigureAwait(false);
                    if (snapshot.Metadata is not null)
                    {
                        var info = archive.CreateEntry("archive.json", CompressionLevel.Fastest);
                        await using var details = info.Open();
                        await JsonSerializer.SerializeAsync(details, snapshot.Metadata with { PageCount = snapshot.Pages.Count }, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }

                    async Task WriteBlob(int page, int index, string kind, byte[] data)
                    {
                        if (data is not { Length: > 0 }) return;
                        if (!blobs.TryGetValue(data, out string name))
                        {
                            name = $"media/{blobs.Count:D6}.bin";
                            blobs.Add(data, name);
                            // Images and videos are already compressed. Store their original bytes once.
                            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                            await using var content = entry.Open();
                            int offset = 0;
                            while (offset < data.Length)
                            {
                                int count = Math.Min(65536, data.Length - offset);
                                await content.WriteAsync(data.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                                offset += count;
                            }
                        }
                        model.Blobs.Add(new(page, index, kind, name, data.LongLength));
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            await PackageCommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite);
            }
            finally { PackageCommitGate.Release(); }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }, cancellationToken);

    /// <summary>Read and validate off the UI thread. The returned action applies the prepared state on the caller's UI thread.</summary>
    public Task<Action> PrepareOpenAsync(string path, bool entireBoard, CancellationToken cancellationToken = default)
    {
        var target = Current;
        var paper = _papers[target];
        return Task.Run(async () =>
        {
            var model = await ReadBoardFileAsync(path, cancellationToken, firstPageOnly: !entireBoard).ConfigureAwait(false);
            var pages = model.Pages;
            if (!entireBoard) pages = new() { pages.FirstOrDefault() ?? new() };
            var fallback = ReadPaper(model.Background, model.GridSpacing, paper);
            var prepared = pages.Select(page => PreparePage(page, fallback)).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            return (Action)(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entireBoard) { ApplyBoard(prepared); ArchiveMetadata = model.Archive; ReplaceExtensions(model.Extensions); }
                else
                {
                    int index = _pages.IndexOf(target);
                    if (index < 0) throw new InvalidOperationException("目标画板页面已关闭。");
                    ReplacePage(index, prepared[0]);
                    ReplaceExtensions(model.Extensions);
                    PageChanged?.Invoke(); ImagesChanged?.Invoke();
                }
            });
        }, cancellationToken);
    }

    public async Task LoadPageAsync(string path, CancellationToken cancellationToken = default) =>
        (await PrepareOpenAsync(path, false, cancellationToken))();

    public async Task LoadBoardAsync(string path, CancellationToken cancellationToken = default) =>
        (await PrepareOpenAsync(path, true, cancellationToken))();

    public Action PrepareImportedPage(string path, bool entireBoard) => PrepareImportedPageAsync(path, entireBoard).GetAwaiter().GetResult();

    public Task<Action> PrepareImportedPageAsync(string path, bool entireBoard, CancellationToken cancellationToken = default)
    {
        var target = Current;
        var paper = _papers[target];
        return Task.Run(async () =>
        {
            var model = await ReadBoardFileAsync(path, cancellationToken, requireSinglePage: true).ConfigureAwait(false);
            if (model.Format != BoardFormat || model.Pages is not { Count: 1 })
                throw new InvalidDataException("文件导入插件未返回有效的单页画板。");
            var file = model.Pages[0];
            if (file.Images?.Any(image => !IsValidImage(image)) == true || file.Media?.Any(media => !IsValidMedia(media)) == true)
                throw new InvalidDataException("文件导入插件返回了无效媒体。");
            var page = PreparePage(file, ReadPaper(model.Background, model.GridSpacing, paper));
            cancellationToken.ThrowIfCancellationRequested();
            return (Action)(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entireBoard) { ApplyBoard(new[] { page }); ReplaceExtensions(model.Extensions); }
                else
                {
                    int index = _pages.IndexOf(target);
                    if (index < 0) throw new InvalidOperationException("目标画板页面已关闭。");
                    ReplacePage(index, page); PageChanged?.Invoke(); ImagesChanged?.Invoke();
                }
            });
        }, cancellationToken);
    }

    private static async Task<BoardFile> ReadBoardFileAsync(string path, CancellationToken cancellationToken,
        bool firstPageOnly = false, bool requireSinglePage = false, int? previewPage = null, bool visualMetadataOnly = false)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var signature = new byte[4];
        int read = await input.ReadAtLeastAsync(signature, signature.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        input.Position = 0;
        bool packaged = read == 4 && signature[0] == 'P' && signature[1] == 'K';
        if (!packaged)
        {
            if (previewPage.HasValue && input.Length > BoardPreview.MaximumManifestBytes) throw new InvalidDataException("旧版文件较大，请打开文件查看。");
            // Deserialize legacy Base64 directly from UTF-8 once, without a UTF-16 string or JsonDocument copy.
            var legacy = await JsonSerializer.DeserializeAsync<BoardFile>(input, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("画板文件为空。");
            if (legacy.Format != BoardFormat)
            {
                if (legacy.Strokes is null) throw new InvalidDataException("文件不是有效的墨迹或画板。");
                legacy.Pages = new() { new() { Strokes = legacy.Strokes } };
            }
            else if (legacy.Version is < 1 or > BoardFormatVersion) throw new InvalidDataException($"不支持的画板版本 {legacy.Version}。");
            ValidatePages(legacy, requireSinglePage);
            if (previewPage.HasValue && (previewPage < 0 || previewPage >= legacy.Pages.Count)) throw new ArgumentOutOfRangeException(nameof(previewPage));
            return legacy;
        }
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("画板包存在重复条目。");
        if (!entries.TryGetValue("board.json", out var manifest)) throw new InvalidDataException("画板包缺少 board.json。");
        if (previewPage.HasValue && manifest.Length > BoardPreview.MaximumManifestBytes) throw new InvalidDataException("页面数据较大，请打开文件查看。");
        BoardFile model;
        await using (var metadata = manifest.Open())
        {
            if (previewPage.HasValue)
            {
                var bytes = new byte[manifest.Length];
                await metadata.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                model = JsonSerializer.Deserialize<BoardFile>(bytes, PackageJsonOptions);
            }
            else model = await JsonSerializer.DeserializeAsync<BoardFile>(metadata, PackageJsonOptions, cancellationToken).ConfigureAwait(false);
            if (model is null) throw new InvalidDataException("画板清单为空。");
        }
        if (model.Format != BoardFormat || model.Version is not (2 or BoardFormatVersion)) throw new InvalidDataException("不支持的画板包格式或版本。");
        ValidatePages(model, requireSinglePage);
        if (previewPage.HasValue && (previewPage < 0 || previewPage >= model.Pages.Count)) throw new ArgumentOutOfRangeException(nameof(previewPage));
        long previewBytes = 0;
        var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (entries.TryGetValue("archive.json", out var archiveInfo)) model.Archive = await ReadArchiveInfoAsync(archiveInfo, cancellationToken).ConfigureAwait(false);
        if (!previewPage.HasValue && !visualMetadataOnly)
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("extensions/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".bin", StringComparison.Ordinal)))
            {
                string id = entry.FullName[11..^4];
                if (id.Length == 0 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-'))
                    || entry.Length > Array.MaxLength) continue;
                var data = new byte[checked((int)entry.Length)];
                await using var content = entry.Open();
                await content.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
                model.Extensions[id] = data;
            }
        var assigned = new HashSet<(int Page, int Index, string Kind)>();
        foreach (var reference in model.Blobs ?? new())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.Page < 0 || reference.Page >= model.Pages.Count || reference.Index < 0
                || reference.Entry is null || !reference.Entry.StartsWith("media/", StringComparison.Ordinal)
                || !assigned.Add((reference.Page, reference.Index, reference.Kind)))
                throw new InvalidDataException("画板媒体引用无效。");
            var page = model.Pages[reference.Page];
            if (reference.Kind == "image" ? reference.Index >= page.Images.Count
                : reference.Kind is not ("video" or "poster") || reference.Index >= page.Media.Count)
                throw new InvalidDataException("画板媒体对象不存在。");
            if (!entries.TryGetValue(reference.Entry, out var entry) || reference.Length != entry.Length
                || entry.Length <= 0 || entry.Length > Array.MaxLength)
                throw new InvalidDataException("画板媒体缺失或长度无效。");
            if (firstPageOnly && reference.Page != 0) continue;
            if (visualMetadataOnly || previewPage.HasValue && (reference.Page != previewPage || reference.Kind == "video")) continue;
            if (previewPage.HasValue && (previewBytes += entry.Length) > 64L * 1024 * 1024) throw new InvalidDataException("页面媒体较大，请打开文件查看。");
            if (!loaded.TryGetValue(reference.Entry, out var bytes))
            {
                bytes = new byte[(int)entry.Length];
                await using var content = entry.Open();
                await content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                loaded.Add(reference.Entry, bytes);
            }
            switch (reference.Kind)
            {
                case "image": page.Images[reference.Index].Data = bytes; break;
                case "video": page.Media[reference.Index].Data = bytes; break;
                case "poster": page.Media[reference.Index].PosterData = bytes; break;
            }
        }
        if (firstPageOnly) model.Pages = new() { model.Pages[0] };
        foreach (var page in previewPage.HasValue || visualMetadataOnly ? Enumerable.Empty<BoardPageFile>() : model.Pages)
            if (page.Images.Any(image => !IsValidImage(image)) || page.Media.Any(media => !IsValidMedia(media)))
                throw new InvalidDataException("画板包中的媒体数据不完整。");
        return model;
    }

    public static Task<(int PageCount, byte[] Png)> ReadPagePreviewAsync(string path, int index, CancellationToken token = default) => Task.Run(async () =>
    {
        var model = await ReadBoardFileAsync(path, token, previewPage: index).ConfigureAwait(false);
        var board = new BoardService();
        try
        {
            var paper = board._papers[board.Current];
            var filePage = model.Pages[index];
            // The temporary renderer accepts posters/placeholders without loading video payloads.
            var prepared = PreparePage(filePage, ReadPaper(model.Background, model.GridSpacing, paper)) with { Media = filePage.Media };
            board.ApplyBoard(new[] { prepared });
            return (model.Pages.Count, await BoardPreview.RenderAsync(board, token: token).ConfigureAwait(false));
        }
        finally { foreach (var page in board.Pages) page.Dispose(); }
    }, token);

    public static async Task<BoardArchiveMetadata> ReadArchiveMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var signature = new byte[2];
        if (await input.ReadAtLeastAsync(signature, 2, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < 2
            || signature[0] != 'P' || signature[1] != 'K') return null;
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entry = archive.GetEntry("archive.json");
        return entry is null ? null : await ReadArchiveInfoAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BoardArchiveMetadata> ReadArchiveInfoAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > 65536) return null;
        try
        {
            await using var content = entry.Open();
            return await JsonSerializer.DeserializeAsync<BoardArchiveMetadata>(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { return null; }
    }

    private static void ValidatePages(BoardFile model, bool requireSinglePage)
    {
        if (requireSinglePage && (model.Format != BoardFormat || model.Pages is not { Count: 1 }))
            throw new InvalidDataException("文件导入插件未返回有效的单页画板。");
        model.Pages ??= new();
        if (model.Pages.Count == 0) model.Pages.Add(new());
        if (model.Pages.Any(page => page is null))
            throw new InvalidDataException("画板页面无效。");
        foreach (var page in model.Pages)
        {
            page.Images ??= new(); page.Media ??= new();
        }
    }
}
