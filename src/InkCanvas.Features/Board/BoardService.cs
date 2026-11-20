using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using System.Numerics;
using System.Text.Json;

namespace InkCanvas.Features.Board;

public enum BoardBackground { Black, White, Grid, Lined }

/// <summary>A bitmap or SVG image placed on a board page; position and size are device-independent pixels.</summary>
public sealed class BoardImageAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long? LayerOrder { get; set; }
    public string Name { get; set; } = "Image";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "image/png";
    public float Left { get; set; }
    public float Top { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float Opacity { get; set; } = 1;

    /// <summary>
    /// Cumulative canvas-space transform, the port of the WPF element RenderTransform
    /// TransformGroup (MW_SelectionGestures.cs ApplyElementMatrixTransform). Six row-major
    /// floats (M11,M12,M21,M22,M31,M32); null means identity. Appended, never replaced, so
    /// rotate/flip/gesture deltas compose exactly like the original TransformGroup.Children.Add.
    /// </summary>
    public float[] Transform { get; set; }

    public BoardImageAsset Copy(bool preserveId = true, bool copyData = true) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(),
        LayerOrder = LayerOrder,
        Name = Name,
        Data = copyData ? Data.ToArray() : Data,
        ContentType = ContentType,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        Opacity = Opacity,
        Transform = Transform?.ToArray(),
    };
}

public readonly record struct BoardImageGeometry(float Left, float Top, float Width, float Height);

/// <summary>A video file embedded on a whiteboard page, with DIP placement/size.</summary>
public sealed class BoardMediaAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long? LayerOrder { get; set; }
    public string Name { get; set; } = "Video";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public byte[] PosterData { get; set; } = Array.Empty<byte>();
    public uint NaturalWidth { get; set; }
    public uint NaturalHeight { get; set; }
    public string ContentType { get; set; } = "video/mp4";
    public float Left { get; set; }
    public float Top { get; set; }
    public float Width { get; set; } = 360;
    public float Height { get; set; } = 240;

    /// <inheritdoc cref="BoardImageAsset.Transform"/>
    public float[] Transform { get; set; }

    public BoardMediaAsset Copy(bool preserveId = true, bool copyData = true) => new()
    {
        Id = preserveId ? Id : Guid.NewGuid(), LayerOrder = LayerOrder, Name = Name, Data = copyData ? Data.ToArray() : Data, ContentType = ContentType,
        PosterData = copyData ? PosterData.ToArray() : PosterData, NaturalWidth = NaturalWidth, NaturalHeight = NaturalHeight,
        Left = Left, Top = Top, Width = Width, Height = Height,
        Transform = Transform?.ToArray(),
    };

    public MemoryStream OpenRead() => new(Data, 0, Data.Length, writable: false, publiclyVisible: true);
}

public readonly record struct BoardMediaGeometry(float Left, float Top, float Width, float Height);

/// <summary>
/// Math for the per-element canvas-space transform (WPF RenderTransform TransformGroup,
/// MW_SelectionGestures.cs ApplyElementMatrixTransform / MW_ElementsControls.cs CenterAndScaleElement).
/// Matrices use the row-vector convention shared by WPF and System.Numerics: p' = p * M.
/// </summary>
public static class BoardElementMath
{
    public static Matrix3x2 Read(float[] transform)
        => transform is { Length: 6 }
            ? new Matrix3x2(transform[0], transform[1], transform[2], transform[3], transform[4], transform[5])
            : Matrix3x2.Identity;

    public static float[] Write(Matrix3x2 m)
        => new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };

    /// <summary>WPF TransformGroup.Children.Add: the new matrix acts after the existing ones.</summary>
    public static float[] Append(float[] existing, Matrix3x2 m)
        => Write(Read(existing) * m);

    public static bool Same(float[] a, float[] b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-4f) return false;
        return true;
    }

    /// <summary>Axis-aligned hull of the transformed element rectangle, in canvas DIPs.</summary>
    public static Rect TransformedBounds(float left, float top, float width, float height, float[] transform)
    {
        var m = Read(transform);
        var corners = new[]
        {
            new Vector2(left, top),
            new Vector2(left + width, top),
            new Vector2(left + width, top + height),
            new Vector2(left, top + height),
        };
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in corners)
        {
            var v = Vector2.Transform(c, m);
            minX = MathF.Min(minX, v.X); minY = MathF.Min(minY, v.Y);
            maxX = MathF.Max(maxX, v.X); maxY = MathF.Max(maxY, v.Y);
        }
        return Rect.FromLTRB(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// The WinUI RenderTransform (origin 0,0) that reproduces the canvas-space matrix for a view
    /// placed at Canvas.Left/Top = (left,top): final = M * (p_local + t), so the local matrix keeps
    /// M's linear part and offsets by M*t - t.
    /// </summary>
    public static Matrix3x2 RenderMatrix(float left, float top, float[] transform)
    {
        var m = Read(transform);
        var t = new Vector2(left, top);
        var offset = Vector2.Transform(t, m) - t;
        return new Matrix3x2(m.M11, m.M12, m.M21, m.M22, offset.X, offset.Y);
    }
}

/// <summary>
/// Multi-page blackboard/whiteboard. Every page owns its ink controller, undo history and image
/// collection. Coordinates and sizes in both collections are stored in WinUI device-independent
/// pixels, not physical screen pixels.
/// </summary>
public sealed class BoardService
{
    public const int MaximumPages = 99;
    private const string BoardFormat = "inkcanvas-board";
    private const int BoardFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<InkController> _pages = new();
    private readonly Dictionary<Guid, BoardSelectionElement> _selectionElements = new();
    private readonly List<List<BoardImageAsset>> _images = new() { new List<BoardImageAsset>() };
    private readonly List<List<BoardMediaAsset>> _media = new() { new List<BoardMediaAsset>() };

    public BoardService(InkController initialController = null)
    {
        var page = initialController ?? new InkController();
        _pages.Add(page);
        page.GetSelectableElements = () => GetSelectionElements(page);
        ImagesChanged += () => Current.NotifyElementsChanged();
    }

    public IEnumerable<ISelectionElement> GetSelectionElements() => GetSelectionElements(Current);

    private IEnumerable<ISelectionElement> GetSelectionElements(InkController page)
    {
        int index = _pages.IndexOf(page);
        if (index < 0) yield break;
        var images = _images[index];
        var mediaObjects = _media[index];
        var present = _images.SelectMany(items => items).Select(image => image.Id)
            .Concat(_media.SelectMany(items => items).Select(media => media.Id)).ToHashSet();
        foreach (var id in _selectionElements.Keys.Where(id => !present.Contains(id)).ToArray()) _selectionElements.Remove(id);
        foreach (var image in images)
        {
            if (!_selectionElements.TryGetValue(image.Id, out var element) || !ReferenceEquals(element.Image, image))
                _selectionElements[image.Id] = element = new BoardSelectionElement(this, image, page);
            yield return element;
        }
        foreach (var media in mediaObjects)
        {
            if (!_selectionElements.TryGetValue(media.Id, out var element) || !ReferenceEquals(element.Media, media))
                _selectionElements[media.Id] = element = new BoardSelectionElement(this, media, page);
            yield return element;
        }
    }

    public BoardBackground Background { get; set; } = BoardBackground.Black;

    /// <summary>Grid/lined spacing in device-independent pixels.</summary>
    public float GridSpacing { get; set; } = 40f;

    public int CurrentIndex { get; private set; }

    public int PageCount => _pages.Count;

    public bool HasContent => _pages.Any(page => page.Document.Count > 0)
        || _images.Any(images => images.Count > 0)
        || _media.Any(media => media.Count > 0);

    public InkController Current => _pages[CurrentIndex];

    public IReadOnlyList<InkController> Pages => _pages;

    public IReadOnlyList<BoardImageAsset> CurrentImages => _images[CurrentIndex];

    public IReadOnlyList<BoardMediaAsset> CurrentMedia => _media[CurrentIndex];
    public event Action<BoardImageAsset> ImagePlacementChanged;
    public event Action<BoardMediaAsset> MediaPlacementChanged;
    public void NotifyImagePlacement(BoardImageAsset image) => ImagePlacementChanged?.Invoke(image);
    public void ReplaceCurrentImages(IEnumerable<BoardImageAsset> images)
    {
        // Insertion/deletion undo actions retain this list. Keep its identity when a screen page changes.
        var incoming = images.ToArray();
        _images[CurrentIndex].Clear();
        _images[CurrentIndex].AddRange(incoming);
        _selectionElements.Clear();
        ImagesChanged?.Invoke();
    }
    public void NotifyMediaPlacement(BoardMediaAsset media) => MediaPlacementChanged?.Invoke(media);

    /// <summary>Selection transactions own their undo step; these change membership without nesting history.</summary>
    public int GetImageIndex(InkController page, BoardImageAsset image)
    {
        int index = _pages.IndexOf(page);
        return index < 0 ? -1 : _images[index].IndexOf(image);
    }

    public int GetMediaIndex(InkController page, BoardMediaAsset media)
    {
        int index = _pages.IndexOf(page);
        return index < 0 ? -1 : _media[index].IndexOf(media);
    }

    public void SetImagePresent(InkController page, BoardImageAsset image, bool present, int restoreIndex = -1)
    {
        int index = _pages.IndexOf(page);
        if (index < 0) return;
        if (present)
        {
            if (!_images[index].Contains(image))
                _images[index].Insert(restoreIndex < 0 ? _images[index].Count : Math.Clamp(restoreIndex, 0, _images[index].Count), image);
        }
        else _images[index].Remove(image);
        if (index == CurrentIndex) ImagesChanged?.Invoke();
    }

    public void SetMediaPresent(InkController page, BoardMediaAsset media, bool present, int restoreIndex = -1)
    {
        int index = _pages.IndexOf(page);
        if (index < 0) return;
        if (present)
        {
            if (!_media[index].Contains(media))
                _media[index].Insert(restoreIndex < 0 ? _media[index].Count : Math.Clamp(restoreIndex, 0, _media[index].Count), media);
        }
        else _media[index].Remove(media);
        if (index == CurrentIndex) ImagesChanged?.Invoke();
    }

    public event Action PageChanged;

    public event Action BackgroundChanged;

    public event Action ImagesChanged;

    public void SetBackground(BoardBackground background)
    {
        if (Background == background) return;
        Background = background;
        BackgroundChanged?.Invoke();
    }

    public void NextPage()
    {
        if (CurrentIndex < _pages.Count - 1)
        {
            GoTo(CurrentIndex + 1);
            return;
        }
        if (_pages.Count >= MaximumPages) return;
        var source = Current;
        AddPageAt(_pages.Count);
        CurrentIndex = _pages.Count - 1;
        CarryDrawingState(source, Current);
        PageChanged?.Invoke();
    }

    public void PreviousPage()
    {
        if (CurrentIndex <= 0) return;
        GoTo(CurrentIndex - 1);
    }

    public void GoTo(int index)
    {
        if (index < 0 || index >= _pages.Count || index == CurrentIndex) return;
        var source = Current;
        CurrentIndex = index;
        CarryDrawingState(source, Current);
        PageChanged?.Invoke();
    }

    public bool InsertPageAfterCurrent()
    {
        if (_pages.Count >= MaximumPages) return false;
        var source = Current;
        AddPageAt(CurrentIndex + 1);
        CurrentIndex++;
        CarryDrawingState(source, Current);
        PageChanged?.Invoke();
        return true;
    }

    /// <summary>Transfer prepared pages in one UI update. A rejected batch leaves all pages intact.</summary>
    public bool InsertPagesAfterCurrent(IReadOnlyList<(InkController Ink, IReadOnlyList<BoardImageAsset> Images)> pages)
    {
        if (pages is null || pages.Count == 0 || _pages.Count + pages.Count > MaximumPages) return false;
        if (pages.Any(page => page.Ink is null || _pages.Contains(page.Ink) || page.Images is null || page.Images.Any(image => !IsValidImage(image)))
            || pages.Select(page => page.Ink).Distinct().Count() != pages.Count) throw new ArgumentException("Imported pages must have independent controllers and valid images.", nameof(pages));
        int insertion = CurrentIndex + 1;
        _pages.InsertRange(insertion, pages.Select(page => page.Ink));
        _images.InsertRange(insertion, pages.Select(page => page.Images.ToList()));
        _media.InsertRange(insertion, pages.Select(_ => new List<BoardMediaAsset>()));
        foreach (var page in pages)
        {
            var controller = page.Ink;
            controller.GetSelectableElements = () => GetSelectionElements(controller);
        }
        CurrentIndex = insertion;
        PageChanged?.Invoke(); ImagesChanged?.Invoke();
        return true;
    }

    private static void CarryDrawingState(InkController source, InkController target)
    {
        source.EndPlayback(); target.EndPlayback();
        source.CancelLassoErase();
        source.ClearSelection();
        target.ClearSelection(); target.CloneMode = false;
        target.CurrentAttributes.Color = source.CurrentAttributes.Color;
        target.CurrentAttributes.BaseWidth = source.CurrentAttributes.BaseWidth;
        target.CurrentAttributes.TaperKind = source.CurrentAttributes.TaperKind;
        target.CurrentAttributes.Nib = source.CurrentAttributes.Nib;
        target.CurrentAttributes.IsHighlighter = source.CurrentAttributes.IsHighlighter;
        target.CurrentAttributes.HighlighterAlpha = source.CurrentAttributes.HighlighterAlpha;
        target.EraserMode = source.EraserMode;
        target.EraserRadius = source.EraserRadius;
        target.ShapeKind = source.ShapeKind;
        target.StickyShape = source.StickyShape;
        target.Tool = source.Tool;
    }

    /// <summary>WPF 衍至新页: duplicate every selected object, with one history entry on the new page.</summary>
    public bool CloneSelectionToNewPage()
    {
        if (PageCount >= MaximumPages || !Current.HasSelection) return false;
        var strokes = InkStroke.CloneObjects(Current.Selection);
        var ids = Current.SelectedElements.Select(e => e.Id).ToHashSet();
        var images = CurrentImages.Where(image => ids.Contains(image.Id)).Select(image => image.Copy(preserveId: false, copyData: false)).ToList();
        var media = CurrentMedia.Where(item => ids.Contains(item.Id)).Select(item => item.Copy(preserveId: false, copyData: false)).ToList();
        if (strokes.Count + images.Count + media.Count == 0) return false;
        InsertPageAfterCurrent();
        var page = Current;
        var pageImages = _images[CurrentIndex]; var pageMedia = _media[CurrentIndex];
        Apply();
        page.History.Push(new DelegateAction("clone selection to page", Apply, Revert));
        return true;

        void Apply()
        {
            page.Document.AddRange(strokes); pageImages.AddRange(images); pageMedia.AddRange(media);
            ImagesChanged?.Invoke();
        }
        void Revert()
        {
            page.Document.RemoveAll(strokes); pageImages.RemoveAll(images.Contains); pageMedia.RemoveAll(media.Contains);
            ImagesChanged?.Invoke();
        }
    }

    /// <summary>Clear ink and embedded objects atomically; one undo restores the whole page in order.</summary>
    public void ClearCurrentPage()
    {
        var page = Current;
        page.CancelLassoErase(); page.ClearSelection();
        var pageImages = _images[CurrentIndex]; var pageMedia = _media[CurrentIndex];
        var strokes = page.Document.Strokes.ToList(); var images = pageImages.ToList(); var media = pageMedia.ToList();
        if (strokes.Count + images.Count + media.Count == 0) return;
        Apply();
        page.History.Push(new DelegateAction("clear board page", Apply, Revert));
        void Apply() { page.Document.Clear(); pageImages.Clear(); pageMedia.Clear(); ImagesChanged?.Invoke(); }
        void Revert() { page.Document.AddRange(strokes); pageImages.AddRange(images); pageMedia.AddRange(media); ImagesChanged?.Invoke(); }
    }

    public void RemoveCurrentPage()
    {
        if (_pages.Count == 1)
        {
            Current.Clear();
            Current.Selection.Clear();
            Current.History.Clear();
            _images[0].Clear();
            _media[0].Clear();
            ImagesChanged?.Invoke();
            PageChanged?.Invoke();
            return;
        }

        var source = Current;
        _pages[CurrentIndex].Dispose();
        _pages.RemoveAt(CurrentIndex);
        _images.RemoveAt(CurrentIndex);
        _media.RemoveAt(CurrentIndex);
        CurrentIndex = Math.Clamp(CurrentIndex, 0, _pages.Count - 1);
        CarryDrawingState(source, Current);
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
    }

    public bool AddImage(BoardImageAsset image)
    {
        if (image is null || image.Data is null || image.Data.Length == 0 || image.Width <= 0 || image.Height <= 0)
            return false;

        var pageImages = _images[CurrentIndex];
        int index = pageImages.Count;
        image.LayerOrder = Current.NextLayerOrder();
        pageImages.Add(image);
        Current.History.Push(new DelegateAction("insert image",
            () => { pageImages.Insert(Math.Clamp(index, 0, pageImages.Count), image); ImagesChanged?.Invoke(); },
            () => { pageImages.Remove(image); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
        return true;
    }

    public void RemoveImage(BoardImageAsset image)
    {
        if (image is null) return;
        var pageImages = _images[CurrentIndex];
        int index = pageImages.IndexOf(image);
        if (index < 0) return;
        pageImages.RemoveAt(index);
        Current.History.Push(new DelegateAction("delete image",
            () => { pageImages.Remove(image); ImagesChanged?.Invoke(); },
            () => { pageImages.Insert(Math.Clamp(index, 0, pageImages.Count), image); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
    }

    /// <summary>Record one completed image move/resize as a single page-local undo step.</summary>
    public void RecordImageTransform(BoardImageAsset image, BoardImageGeometry before, BoardImageGeometry after)
    {
        if (image is null || !_images[CurrentIndex].Contains(image) || before == after) return;
        Current.History.Push(new DelegateAction("transform image",
            () => SetImageGeometry(image, after),
            () => SetImageGeometry(image, before)));
        ImagesChanged?.Invoke();
    }

    /// <summary>Insert video bytes as a page-local media object; page undo removes/restores it.</summary>
    public bool AddMedia(BoardMediaAsset media)
    {
        if (!IsValidMedia(media)) return false;
        var pageMedia = _media[CurrentIndex];
        int index = pageMedia.Count;
        media.LayerOrder = Current.NextLayerOrder();
        pageMedia.Add(media);
        Current.History.Push(new DelegateAction("insert media",
            () => { pageMedia.Insert(Math.Clamp(index, 0, pageMedia.Count), media); ImagesChanged?.Invoke(); },
            () => { pageMedia.Remove(media); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
        return true;
    }

    public void RemoveMedia(BoardMediaAsset media)
    {
        if (media is null) return;
        var pageMedia = _media[CurrentIndex];
        int index = pageMedia.IndexOf(media);
        if (index < 0) return;
        pageMedia.RemoveAt(index);
        Current.History.Push(new DelegateAction("delete media",
            () => { pageMedia.Remove(media); ImagesChanged?.Invoke(); },
            () => { pageMedia.Insert(Math.Clamp(index, 0, pageMedia.Count), media); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
    }

    public void RecordMediaTransform(BoardMediaAsset media, BoardMediaGeometry before, BoardMediaGeometry after)
    {
        if (media is null || !_media[CurrentIndex].Contains(media) || before == after) return;
        Current.History.Push(new DelegateAction("transform media",
            () => SetMediaGeometry(media, after),
            () => SetMediaGeometry(media, before)));
        ImagesChanged?.Invoke();
    }

    /// <summary>
    /// Record one completed element change (move/resize/rotate/flip/gesture) as a single
    /// page-local undo step. <paramref name="before"/> is a <see cref="BoardImageAsset.Copy"/>
    /// snapshot taken by the caller at gesture start; the after-state is captured here.
    /// Port of WPF TimeMachine.CommitStrokeManipulationHistory for elements.
    /// </summary>
    public void RecordImageChange(BoardImageAsset image, BoardImageAsset before, string name)
    {
        if (image is null || before is null || !_images[CurrentIndex].Contains(image)) return;
        var after = image.Copy();
        if (SameImageState(before, after)) return;
        Current.History.Push(new DelegateAction(name,
            () => { ApplyImageState(image, after); ImagesChanged?.Invoke(); },
            () => { ApplyImageState(image, before); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
    }

    /// <inheritdoc cref="RecordImageChange"/>
    public void RecordMediaChange(BoardMediaAsset media, BoardMediaAsset before, string name)
    {
        if (media is null || before is null || !_media[CurrentIndex].Contains(media)) return;
        var after = media.Copy();
        if (SameMediaState(before, after)) return;
        Current.History.Push(new DelegateAction(name,
            () => { ApplyMediaState(media, after); ImagesChanged?.Invoke(); },
            () => { ApplyMediaState(media, before); ImagesChanged?.Invoke(); }));
        ImagesChanged?.Invoke();
    }

    private static bool SameImageState(BoardImageAsset a, BoardImageAsset b)
        => a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height
           && a.Opacity == b.Opacity
           && BoardElementMath.Same(a.Transform, b.Transform);

    private static bool SameMediaState(BoardMediaAsset a, BoardMediaAsset b)
        => a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height
           && BoardElementMath.Same(a.Transform, b.Transform);

    private static void ApplyImageState(BoardImageAsset image, BoardImageAsset snapshot)
    {
        image.Left = snapshot.Left;
        image.Top = snapshot.Top;
        image.Width = snapshot.Width;
        image.Height = snapshot.Height;
        image.Opacity = snapshot.Opacity;
        image.Transform = snapshot.Transform?.ToArray();
    }

    private static void ApplyMediaState(BoardMediaAsset media, BoardMediaAsset snapshot)
    {
        media.Left = snapshot.Left;
        media.Top = snapshot.Top;
        media.Width = snapshot.Width;
        media.Height = snapshot.Height;
        media.Transform = snapshot.Transform?.ToArray();
    }

    public void SavePage(string path)
    {
        if (_images[CurrentIndex].Count == 0 && _media[CurrentIndex].Count == 0)
        {
            InkFileStore.Save(path, Current.Document.Strokes);
            return;
        }
        SaveBoardFile(path, new[] { CurrentIndex });
    }

    public void SaveBoard(string path) => SaveBoardFile(path, Enumerable.Range(0, _pages.Count));

    /// <summary>Load a legacy single-page .icink or a board/page package saved by this service.</summary>
    public void LoadPage(string path)
    {
        if (TryReadBoardFile(path, out var model))
        {
            var page = model.Pages?.FirstOrDefault() ?? new BoardPageFile();
            ReplacePage(CurrentIndex, page);
        }
        else
        {
            ReplacePage(CurrentIndex, new BoardPageFile
            {
                Strokes = InkFileStore.Load(path).Select(ToStrokeFile).ToList(),
            });
        }
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
    }

    /// <summary>Open a board package, or treat a legacy .icink as a one-page board.</summary>
    public void LoadBoard(string path)
    {
        List<BoardPageFile> loadedPages;
        BoardBackground background = Background;
        float gridSpacing = GridSpacing;
        if (TryReadBoardFile(path, out var model))
        {
            loadedPages = model.Pages ?? new List<BoardPageFile>();
            if (loadedPages.Count == 0) loadedPages.Add(new BoardPageFile());
            if (loadedPages.Count > MaximumPages)
                throw new InvalidDataException($"A board can contain at most {MaximumPages} pages.");
            if (Enum.TryParse(model.Background, ignoreCase: true, out BoardBackground parsed)) background = parsed;
            if (model.GridSpacing > 0) gridSpacing = model.GridSpacing;
        }
        else
        {
            loadedPages = new List<BoardPageFile>
            {
                new() { Strokes = InkFileStore.Load(path).Select(ToStrokeFile).ToList() },
            };
        }

        foreach (var page in _pages) page.Dispose();
        _pages.Clear();
        _images.Clear();
        _media.Clear();
        foreach (var page in loadedPages)
        {
            var controller = new InkController();
            controller.GetSelectableElements = () => GetSelectionElements(controller);
            controller.Document.AddRange((page.Strokes ?? new List<StrokeFile>()).Select(FromStrokeFile));
            _pages.Add(controller);
            _images.Add((page.Images ?? new List<BoardImageAsset>()).Where(IsValidImage).ToList());
            _media.Add((page.Media ?? new List<BoardMediaAsset>()).Where(IsValidMedia).ToList());
        }
        CurrentIndex = 0;
        GridSpacing = Math.Clamp(gridSpacing, 12f, 160f);
        if (Background != background)
        {
            Background = background;
            BackgroundChanged?.Invoke();
        }
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
    }

    private void SaveBoardFile(string path, IEnumerable<int> pageIndices)
    {
        var file = new BoardFile
        {
            Format = BoardFormat,
            Version = BoardFormatVersion,
            Background = Background.ToString(),
            GridSpacing = GridSpacing,
            Pages = pageIndices.Select(index => new BoardPageFile
            {
                Strokes = _pages[index].Document.Strokes.Select(ToStrokeFile).ToList(),
                Images = _images[index].ToList(),
                Media = _media[index].ToList(),
            }).ToList(),
        };
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool TryReadBoardFile(string path, out BoardFile model)
    {
        model = null;
        if (!File.Exists(path)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("Format", out var format) || format.GetString() != BoardFormat)
            return false;
        model = JsonSerializer.Deserialize<BoardFile>(document.RootElement.GetRawText(), JsonOptions)
            ?? throw new InvalidDataException("The board file is empty or invalid.");
        if (model.Version != BoardFormatVersion)
            throw new InvalidDataException($"Unsupported board file version {model.Version}.");
        return true;
    }

    private void ReplacePage(int index, BoardPageFile page)
    {
        var controller = _pages[index];
        controller.CancelLassoErase(); controller.ClearSelection();
        controller.Document.Clear();
        controller.History.Clear();
        controller.ViewTransform = Matrix3x2.Identity;
        controller.Document.AddRange((page.Strokes ?? new List<StrokeFile>()).Select(FromStrokeFile));
        _images[index] = (page.Images ?? new List<BoardImageAsset>()).Where(IsValidImage).ToList();
        _media[index] = (page.Media ?? new List<BoardMediaAsset>()).Where(IsValidMedia).ToList();
    }

    private void AddPageAt(int index)
    {
        var page = new InkController();
        page.GetSelectableElements = () => GetSelectionElements(page);
        _pages.Insert(index, page);
        _images.Insert(index, new List<BoardImageAsset>());
        _media.Insert(index, new List<BoardMediaAsset>());
    }

    private static bool IsValidImage(BoardImageAsset image)
        => image is not null && image.Data is { Length: > 0 } && image.Width > 0 && image.Height > 0;

    private static bool IsValidMedia(BoardMediaAsset media)
        => media is not null && media.Data is { Length: > 0 } && media.Width > 0 && media.Height > 0
           && media.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    private void SetImageGeometry(BoardImageAsset image, BoardImageGeometry geometry)
    {
        image.Left = geometry.Left;
        image.Top = geometry.Top;
        image.Width = geometry.Width;
        image.Height = geometry.Height;
        ImagesChanged?.Invoke();
    }

    private void SetMediaGeometry(BoardMediaAsset media, BoardMediaGeometry geometry)
    {
        media.Left = geometry.Left;
        media.Top = geometry.Top;
        media.Width = geometry.Width;
        media.Height = geometry.Height;
        ImagesChanged?.Invoke();
    }

    private static StrokeFile ToStrokeFile(InkStroke stroke) => new()
    {
        LayerOrder = stroke.LayerOrder,
        Color = stroke.Attributes.Color.PackedArgb,
        Width = stroke.Attributes.BaseWidth,
        Highlighter = stroke.Attributes.IsHighlighter,
        Taper = (int)stroke.Attributes.TaperKind,
        Nib = stroke.Attributes.Nib,
        NibRotation = stroke.Attributes.NibRotation,
        Shape = stroke.IsRecognizedShape,
        ShapeSource = stroke.ShapeSource,
        HighlighterAlpha = stroke.Attributes.HighlighterAlpha,
        ObjectFrame = stroke.ExportObjectFrame(),
        Points = stroke.Points.Select(point => point.HasPressure || point.TiltX != 0 || point.TiltY != 0
            ? new[] { point.X, point.Y, point.Pressure, point.Width, point.TimeMs, point.TiltX, point.TiltY, point.HasPressure ? 1f : 0f }
            : new[] { point.X, point.Y, point.Pressure, point.Width, point.TimeMs }).ToList(),
    };

    private static InkStroke FromStrokeFile(StrokeFile strokeFile)
    {
        var attributes = new StrokeAttributes
        {
            Color = InkColor.FromPackedArgb(strokeFile.Color),
            BaseWidth = strokeFile.Width,
            Nib = Enum.IsDefined(strokeFile.Nib) ? strokeFile.Nib : PenNib.Round,
            NibRotation = float.IsFinite(strokeFile.NibRotation) ? strokeFile.NibRotation : 0,
            IsHighlighter = strokeFile.Highlighter,
            HighlighterAlpha = strokeFile.HighlighterAlpha,
            TaperKind = Enum.IsDefined(typeof(TaperKind), strokeFile.Taper)
                ? (TaperKind)strokeFile.Taper : TaperKind.None,
        };
        var stroke = new InkStroke(attributes) { IsRecognizedShape = strokeFile.Shape, LayerOrder = strokeFile.LayerOrder, ShapeSource = strokeFile.ShapeSource };
        stroke.RestorePoints((strokeFile.Points ?? new List<float[]>())
            .Where(row => row.Length >= 4)
            .Select(row => new InkPoint(row[0], row[1], row[2], row[3], row.Length > 4 ? row[4] : 0f,
                row.Length > 7 && row[7] > 0, row.Length > 5 ? row[5] : 0, row.Length > 6 ? row[6] : 0)));
        stroke.Complete();
        stroke.ImportObjectFrame(strokeFile.ObjectFrame);
        return stroke;
    }

    private sealed class BoardFile
    {
        public string Format { get; set; }
        public int Version { get; set; }
        public string Background { get; set; }
        public float GridSpacing { get; set; }
        public List<BoardPageFile> Pages { get; set; } = new();
    }

    private sealed class BoardPageFile
    {
        public List<StrokeFile> Strokes { get; set; } = new();
        public List<BoardImageAsset> Images { get; set; } = new();
        public List<BoardMediaAsset> Media { get; set; } = new();
    }

    private sealed class StrokeFile
    {
        public long? LayerOrder { get; set; }
        public uint Color { get; set; }
        public float Width { get; set; }
        public bool Highlighter { get; set; }
        public int Taper { get; set; }
        public PenNib Nib { get; set; }
        public float NibRotation { get; set; }
        public bool Shape { get; set; }
        public ShapeSource ShapeSource { get; set; }
        public byte HighlighterAlpha { get; set; } = 96;
        public float[] ObjectFrame { get; set; }
        public List<float[]> Points { get; set; } = new();
    }
}
