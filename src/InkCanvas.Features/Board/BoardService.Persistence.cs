using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board.AiObjects;

namespace InkCanvas.Features.Board;

public sealed partial class BoardService
{
    private sealed record BoardSnapshot(List<PreparedPage> Pages);
    private sealed record SideBoardSnapshot(int PageIndex, List<PreparedPage> Pages);
    private sealed record BoardBlobReference(int Page, int Index, string Kind, string Entry, long Length)
    {
        public string Scope { get; init; }
    }

    /// <summary>Legacy single-page file (no board header, plain JSON with a Strokes array).</summary>
    private sealed class LegacyBoardFile
    {
        public List<StrokeFile> Strokes { get; set; } = new();
    }

    private static readonly JsonSerializerOptions PackageJsonOptions = CreatePackageJsonOptions();
    private static readonly JsonSerializerOptions PrettyPackageJsonOptions = new(PackageJsonOptions) { WriteIndented = true };
    private static readonly SemaphoreSlim PackageCommitGate = new(1, 1);
    private Task _saveQueue = Task.CompletedTask;

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

    public Task SavePageAsync(string path, CancellationToken cancellationToken = default) =>
        QueueSave(path, CaptureSnapshot(new[] { CurrentIndex }), null, cancellationToken, allowLegacySinglePage: true);

    public Task SaveBoardAsync(string path, CancellationToken cancellationToken = default)
    {
        var side = CaptureSideSnapshot();
        return QueueSave(path, CaptureSnapshot(Enumerable.Range(0, PageCount)), side, cancellationToken);
    }

    private Task QueueSave(string path, BoardSnapshot snapshot, SideBoardSnapshot side, CancellationToken cancellationToken,
        bool allowLegacySinglePage = false)
    {
        var previous = _saveQueue;
        return _saveQueue = SaveAfterPreviousAsync();

        async Task SaveAfterPreviousAsync()
        {
            // Keep request order so an older large snapshot cannot overwrite a newer small one.
            try { await previous.ConfigureAwait(false); }
            catch (Exception) { }
            await SaveSnapshotAsync(path, snapshot, side, cancellationToken, allowLegacySinglePage).ConfigureAwait(false);
        }
    }

    private BoardSnapshot CaptureSnapshot(IEnumerable<int> indices) => new(
        indices.Select(index => new PreparedPage(
            _pages[index].Document.Strokes.Select(stroke => stroke.Clone()).ToList(),
            _images[index].Select(image => image.Copy(copyData: false)).ToList(),
            _media[index].Select(media => media.Copy(copyData: false)).ToList(), _papers[_pages[index]],
            _states[index].Name, ToTransformArray(_pages[index].ViewTransform),
            _textObjects[index].Select(text => text.Copy()).ToList(),
            _formulaObjects[index].Select(formula => formula.Copy()).ToList())).ToList());

    private SideBoardSnapshot CaptureSideSnapshot()
    {
        var side = SideBoardSource;
        if (side is null || ReferenceEquals(side, this)) return null;
        return new(side.CurrentIndex, side.CaptureSnapshot(Enumerable.Range(0, side.PageCount)).Pages);
    }

    private static BoardPageFile ToPageFile(PreparedPage page) => new()
    {
        Name = page.Name,
        Background = page.Paper.Background.ToString(), GridSpacing = page.Paper.GridSpacing,
        ViewTransform = page.ViewTransform,
        Strokes = page.Strokes.Select(ToStrokeFile).ToList(), Images = page.Images, Media = page.Media,
        TextObjects = (page.TextObjects ?? new()).Select(BoardAiObjectSerialization.ToFile).ToList(),
        FormulaObjects = (page.FormulaObjects ?? new()).Select(BoardAiObjectSerialization.ToFile).ToList(),
    };

    private static Task SaveSnapshotAsync(string path, BoardSnapshot snapshot, SideBoardSnapshot side, CancellationToken cancellationToken,
        bool allowLegacySinglePage) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (allowLegacySinglePage && side is null && TryBuildLegacySinglePage(snapshot, out var legacy))
            {
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await JsonSerializer.SerializeAsync(output, legacy, JsonOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var model = new BoardFile
                {
                    Format = BoardFormat, Version = BoardFormatVersion,
                    Pages = snapshot.Pages.Select(ToPageFile).ToList(),
                    SideBoard = side is null ? null : new BoardSideFile
                    {
                        Version = SideBoardFormatVersion,
                        PageIndex = side.PageIndex,
                        Pages = side.Pages.Select(ToPageFile).ToList(),
                    },
                };
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var blobs = new Dictionary<byte[], string>(ReferenceEqualityComparer.Instance);
                        await WritePageBlobs("main", model.Pages);
                        if (model.SideBoard is not null) await WritePageBlobs("side", model.SideBoard.Pages);

                        async Task WritePageBlobs(string scope, List<BoardPageFile> pages)
                        {
                            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
                            {
                                var page = pages[pageIndex];
                                for (int index = 0; index < page.Images.Count; index++)
                                    await WriteBlob(scope, pageIndex, index, "image", page.Images[index].Data);
                                for (int index = 0; index < page.Media.Count; index++)
                                {
                                    await WriteBlob(scope, pageIndex, index, "video", page.Media[index].Data);
                                    await WriteBlob(scope, pageIndex, index, "poster", page.Media[index].PosterData);
                                }
                            }
                        }
                        var manifest = archive.CreateEntry("board.json", CompressionLevel.Fastest);
                        await using (var metadata = manifest.Open())
                            await JsonSerializer.SerializeAsync(metadata, model, PackageJsonOptions, cancellationToken).ConfigureAwait(false);

                        // Uncompressed, indented manifest copy for tooling that inspects the package as text.
                        var readable = archive.CreateEntry("board.pretty.json", CompressionLevel.NoCompression);
                        await using (var pretty = readable.Open())
                            await JsonSerializer.SerializeAsync(pretty, model, PrettyPackageJsonOptions, cancellationToken).ConfigureAwait(false);

                        async Task WriteBlob(string scope, int page, int index, string kind, byte[] data)
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
                            model.Blobs.Add(new(page, index, kind, name, data.LongLength) { Scope = scope });
                        }
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await PackageCommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally { PackageCommitGate.Release(); }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }, cancellationToken);

    /// <summary>
    /// Legacy single-page JSON has no header and no per-page metadata, so only default, media-free,
    /// one-page snapshots are downgraded; anything else keeps the v4 package format.
    /// </summary>
    private static bool TryBuildLegacySinglePage(BoardSnapshot snapshot, out LegacyBoardFile legacy)
    {
        legacy = null;
        if (snapshot.Pages.Count != 1) return false;
        var page = snapshot.Pages[0];
        if (page.Images.Count > 0 || page.Media.Count > 0) return false;
        if (page.Paper.Background != BoardBackground.Black || Math.Abs(page.Paper.GridSpacing - 40f) > 0.001f) return false;
        if (FromTransformArray(page.ViewTransform) != Matrix3x2.Identity) return false;
        if (!string.IsNullOrEmpty(page.Name) && page.Name != "页面 1") return false;
        legacy = new LegacyBoardFile { Strokes = page.Strokes.Select(ToStrokeFile).ToList() };
        return true;
    }

    /// <summary>Read and validate off the UI thread. The returned action applies the prepared state on the caller's UI thread.</summary>
    public Task<Action> PrepareOpenAsync(string path, bool entireBoard, CancellationToken cancellationToken = default)
    {
        var target = Current;
        var paper = _papers[target];
        var sideTarget = entireBoard ? SideBoardSource : null;
        if (sideTarget is not null && ReferenceEquals(sideTarget, this)) sideTarget = null;
        BoardPaper sidePaper = sideTarget is null ? default : sideTarget._papers[sideTarget.Current];
        return Task.Run(async () =>
        {
            var model = await ReadBoardFileAsync(path, cancellationToken, firstPageOnly: !entireBoard).ConfigureAwait(false);
            var pages = model.Pages;
            if (!entireBoard) pages = new() { pages.FirstOrDefault() ?? new() };
            var fallback = ReadPaper(model.Background, model.GridSpacing, paper);
            var prepared = pages.Select(page => PreparePage(page, fallback)).ToList();
            List<PreparedPage> preparedSide = null;
            int sideIndex = 0;
            if (entireBoard && sideTarget is not null && model.SideBoard is not null)
            {
                preparedSide = model.SideBoard.Pages.Select(page => PreparePage(page, sidePaper)).ToList();
                sideIndex = model.SideBoard.PageIndex;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (Action)(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entireBoard) ApplyBoard(prepared);
                else
                {
                    int index = _pages.IndexOf(target);
                    if (index < 0) throw new InvalidOperationException("目标画板页面已关闭。");
                    ReplacePage(index, prepared[0]);
                    PageChanged?.Invoke(); ImagesChanged?.Invoke();
                }
                if (!entireBoard) return;
                // 应用前校验目标子板服务仍是打开请求发起时的那一个，避免主/子板串写。
                if (sideTarget is null || !ReferenceEquals(sideTarget, SideBoardSource)) return;
                if (preparedSide is not null) sideTarget.ApplyBoard(preparedSide, sideIndex);
                else sideTarget.ApplyBoard(new[] { new PreparedPage(new(), new(), new(), fallback, null, null) }, 0);
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
                if (entireBoard) ApplyBoard(new[] { page });
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
        bool firstPageOnly = false, bool requireSinglePage = false)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var signature = new byte[4];
        int read = await input.ReadAtLeastAsync(signature, signature.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        input.Position = 0;
        bool packaged = read == 4 && signature[0] == 'P' && signature[1] == 'K';
        if (!packaged)
        {
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
            return legacy;
        }
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
            if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("画板包存在重复条目。");
        if (!entries.TryGetValue("board.json", out var manifest)) throw new InvalidDataException("画板包缺少 board.json。");
        BoardFile model;
        await using (var metadata = manifest.Open())
            model = await JsonSerializer.DeserializeAsync<BoardFile>(metadata, PackageJsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("画板清单为空。");
        if (model.Format != BoardFormat || model.Version is not (2 or 3 or BoardFormatVersion)) throw new InvalidDataException("不支持的画板包格式或版本。");
        ValidatePages(model, requireSinglePage);
        var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var assigned = new HashSet<(string Scope, int Page, int Index, string Kind)>();
        foreach (var reference in model.Blobs ?? new())
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool sideScoped = string.Equals(reference.Scope, "side", StringComparison.Ordinal);
            if (!sideScoped && reference.Scope is not (null or "" or "main"))
                throw new InvalidDataException("画板媒体引用无效。");
            var ownerPages = sideScoped ? model.SideBoard?.Pages : model.Pages;
            if (ownerPages is null || reference.Page < 0 || reference.Page >= ownerPages.Count || reference.Index < 0
                || reference.Entry is null || !reference.Entry.StartsWith("media/", StringComparison.Ordinal)
                || !assigned.Add((reference.Scope ?? "main", reference.Page, reference.Index, reference.Kind)))
                throw new InvalidDataException("画板媒体引用无效。");
            var page = ownerPages[reference.Page];
            if (reference.Kind == "image" ? reference.Index >= page.Images.Count
                : reference.Kind is not ("video" or "poster") || reference.Index >= page.Media.Count)
                throw new InvalidDataException("画板媒体对象不存在。");
            if (!entries.TryGetValue(reference.Entry, out var entry) || reference.Length != entry.Length
                || entry.Length <= 0 || entry.Length > Array.MaxLength)
                throw new InvalidDataException("画板媒体缺失或长度无效。");
            if (firstPageOnly && (sideScoped || reference.Page != 0)) continue;
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
        foreach (var page in model.Pages)
            if (page.Images.Any(image => !IsValidImage(image)) || page.Media.Any(media => !IsValidMedia(media)))
                throw new InvalidDataException("画板包中的媒体数据不完整。");
        if (!firstPageOnly && model.SideBoard is not null)
            foreach (var page in model.SideBoard.Pages)
                if (page.Images.Any(image => !IsValidImage(image)) || page.Media.Any(media => !IsValidMedia(media)))
                    throw new InvalidDataException("板中板媒体数据不完整。");
        return model;
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
            page.TextObjects ??= new(); page.FormulaObjects ??= new();
        }
        ValidateSideBoard(model);
    }

    private static void ValidateSideBoard(BoardFile model)
    {
        if (model.SideBoard is null) return;
        if (model.SideBoard.Version is < 1 or > SideBoardFormatVersion)
            throw new InvalidDataException($"不支持的板中板版本 {model.SideBoard.Version}。");
        model.SideBoard.Pages ??= new();
        if (model.SideBoard.Pages.Count == 0) model.SideBoard.Pages.Add(new());
        if (model.SideBoard.Pages.Any(page => page is null))
            throw new InvalidDataException("板中板页面无效。");
        foreach (var page in model.SideBoard.Pages) { page.Images ??= new(); page.Media ??= new(); page.TextObjects ??= new(); page.FormulaObjects ??= new(); }
    }
}
