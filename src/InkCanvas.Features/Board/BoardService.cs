using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using System.Numerics;
using System.Text.Json;

namespace InkCanvas.Features.Board;

public enum BoardBackground { Black, White, Grid, Lined, Green, Cream, Coordinate }

/// <summary>Lightweight page summary for thumbnail/list UIs (no bitmap rendering required).</summary>
public readonly record struct BoardPageThumbnail(
    int Index, string Name, BoardBackground Background, int StrokeCount, int ImageCount);

public sealed record BoardPageInfo(InkController Controller, BoardBackground Background, float GridSpacing,
    IReadOnlyList<BoardImageAsset> Images, IReadOnlyList<BoardMediaAsset> Media);

/// <summary>A bitmap or SVG image placed on a board page; position and size are device-independent pixels.</summary>
public sealed class BoardImageAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long? LayerOrder { get; set; }
    public string Name { get; set; } = "Image";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Metadata { get; set; } = "";
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
        Metadata = Metadata,
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
public sealed partial class BoardService
{
    private const string BoardFormat = "inkcanvas-board";
    private const int BoardFormatVersion = 4;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<InkController> _pages = new();
    private readonly UndoStack _pageHistory = new(100);
    private int _nextPageNumber = 2;
    private sealed record BoardPaper(BoardBackground Background = BoardBackground.Black, float GridSpacing = 40f, string Name = null);
    private readonly Dictionary<InkController, BoardPaper> _papers = new();
    private readonly Dictionary<Guid, BoardSelectionElement> _selectionElements = new();
    private readonly List<List<BoardImageAsset>> _images = new() { new List<BoardImageAsset>() };
    private readonly List<List<BoardMediaAsset>> _media = new() { new List<BoardMediaAsset>() };

    public BoardService(InkController initialController = null)
    {
        var page = initialController ?? new InkController();
        _pages.Add(page);
        _papers.Add(page, new(Name: "页面 1"));
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
        var present = images.Select(image => image.Id).Concat(mediaObjects.Select(media => media.Id)).ToHashSet();
        foreach (var id in _selectionElements.Where(pair => ReferenceEquals(pair.Value.Page, page) && !present.Contains(pair.Key))
            .Select(pair => pair.Key).ToArray()) _selectionElements.Remove(id);
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

    /// <summary>The current page's paper. Changing it does not modify other pages.</summary>
    public BoardBackground Background
    {
        get => _papers[Current].Background;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (Background == value) return;
            _papers[Current] = _papers[Current] with { Background = value };
            BackgroundChanged?.Invoke();
        }
    }

    /// <summary>Grid/lined spacing in device-independent pixels.</summary>
    public float GridSpacing
    {
        get => _papers[Current].GridSpacing;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            value = Math.Clamp(value, 12f, 160f);
            if (GridSpacing == value) return;
            _papers[Current] = _papers[Current] with { GridSpacing = value };
            BackgroundChanged?.Invoke();
        }
    }

    public int CurrentIndex { get; private set; }

    public int PageCount => _pages.Count;
    public long PageStructureVersion { get; private set; }

    public BoardPageInfo GetPageInfo(int index)
    {
        var page = _pages[index]; var paper = _papers[page];
        return new(page, paper.Background, paper.GridSpacing, _images[index], _media[index]);
    }

    /// <summary>Only the runtime collection capacity limits page creation.</summary>
    public bool CanAddPages(int count = 1) => count > 0 && count <= Array.MaxLength - PageCount;

    public bool HasContent => SideBoard?.HasContent == true || _extensions.Count > 0 || _pages.Any(page => page.Document.Count > 0)
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
        Background = background;
    }

    public void NextPage()
    {
        if (CurrentIndex < _pages.Count - 1)
        {
            GoTo(CurrentIndex + 1);
            return;
        }
        if (!CanAddPages()) return;
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
        if (!CanAddPages()) return false;
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
        if (pages is null || !CanAddPages(pages.Count)) return false;
        if (pages.Any(page => page.Ink is null || _pages.Contains(page.Ink) || page.Images is null || page.Images.Any(image => !IsValidImage(image)))
            || pages.Select(page => page.Ink).Distinct().Count() != pages.Count) throw new ArgumentException("Imported pages must have independent controllers and valid images.", nameof(pages));
        int insertion = CurrentIndex + 1;
        var paper = _papers[Current];
        _pages.InsertRange(insertion, pages.Select(page => page.Ink));
        _images.InsertRange(insertion, pages.Select(page => page.Images.ToList()));
        _media.InsertRange(insertion, pages.Select(_ => new List<BoardMediaAsset>()));
        PageStructureVersion++;
        foreach (var page in pages)
        {
            var controller = page.Ink;
            _papers.Add(controller, paper with { Name = NextPageName() });
            controller.GetSelectableElements = () => GetSelectionElements(controller);
        }
        CurrentIndex = insertion;
        PageChanged?.Invoke(); ImagesChanged?.Invoke();
        return true;
    }

    private static void CarryDrawingState(InkController source, InkController target)
    {
        source.NotifyInputContextChanging();
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
        target.PolygonVertexCount = source.PolygonVertexCount;
        target.StickyShape = source.StickyShape;
        target.Tool = source.Tool;
    }

    /// <summary>WPF 衍至新页: duplicate every selected object, with one history entry on the new page.</summary>
    public bool CloneSelectionToNewPage()
    {
        if (!CanAddPages() || !Current.HasSelection) return false;
        var strokes = InkStroke.CloneObjects(Current.Selection);
        var ids = Current.SelectedElements.Select(e => e.Id).ToHashSet();
        var images = CurrentImages.Where(image => ids.Contains(image.Id)).Select(image => image.Copy(preserveId: false, copyData: false)).ToList();
        var media = CurrentMedia.Where(item => ids.Contains(item.Id)).Select(item => item.Copy(preserveId: false, copyData: false)).ToList();
        if (strokes.Count + images.Count + media.Count == 0) return false;
        if (!InsertPageAfterCurrent()) return false;
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

    public bool CanUndoPage => _pageHistory.CanUndo;
    public bool CanRedoPage => _pageHistory.CanRedo;
    public bool UndoPage() => _pageHistory.Undo();
    public bool RedoPage() => _pageHistory.Redo();

    private string NextPageName() => $"页面 {_nextPageNumber++}";

    public string PageName(int index) => index >= 0 && index < PageCount
        ? _papers[_pages[index]].Name ?? $"页面 {index + 1}" : string.Empty;

    public BoardBackground PageBackground(int index) => index >= 0 && index < PageCount
        ? _papers[_pages[index]].Background : BoardBackground.Black;

    public float PageGridSpacing(int index) => index >= 0 && index < PageCount
        ? _papers[_pages[index]].GridSpacing : 0f;

    public BoardPageThumbnail GetPageThumbnail(int index) => new(
        index, PageName(index), PageBackground(index), _pages[index].Document.Count, _images[index].Count);

    public bool RenamePage(int index, string name)
    {
        string trimmed = name?.Trim();
        if (index < 0 || index >= PageCount || string.IsNullOrEmpty(trimmed) || PageName(index) == trimmed) return false;
        var page = _pages[index];
        string previous = PageName(index);
        _pageHistory.Push(new DelegateAction("重命名页面", () => Rename(trimmed), () => Rename(previous)));
        Rename(trimmed);
        return true;

        void Rename(string value)
        {
            _papers[page] = _papers[page] with { Name = value };
            PageStructureVersion++;
            PageChanged?.Invoke();
        }
    }

    public bool MovePage(int sourceIndex, int targetIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= PageCount || targetIndex < 0 || targetIndex >= PageCount || sourceIndex == targetIndex) return false;
        var page = _pages[sourceIndex];
        _pageHistory.Push(new DelegateAction("移动页面",
            () => MovePageCore(_pages.IndexOf(page), targetIndex), () => MovePageCore(_pages.IndexOf(page), sourceIndex)));
        MovePageCore(sourceIndex, targetIndex);
        return true;
    }

    private void MovePageCore(int sourceIndex, int targetIndex)
    {
        var current = Current;
        var page = _pages[sourceIndex]; var images = _images[sourceIndex]; var media = _media[sourceIndex];
        _pages.RemoveAt(sourceIndex); _images.RemoveAt(sourceIndex); _media.RemoveAt(sourceIndex);
        _pages.Insert(targetIndex, page); _images.Insert(targetIndex, images); _media.Insert(targetIndex, media);
        CurrentIndex = _pages.IndexOf(current);
        PageStructureVersion++;
        PageChanged?.Invoke();
    }

    public bool CopyCurrentPage() => DuplicateCurrentPage(copyData: true);

    public bool DuplicateCurrentPage() => DuplicateCurrentPage(copyData: false);

    private bool DuplicateCurrentPage(bool copyData)
    {
        if (!CanAddPages()) return false;
        var source = Current;
        var strokes = InkStroke.CloneObjects(source.Document.Strokes);
        var images = CurrentImages.Select(image => image.Copy(preserveId: false, copyData: copyData)).ToList();
        var media = CurrentMedia.Select(item => item.Copy(preserveId: false, copyData: copyData)).ToList();
        AddPageAt(CurrentIndex + 1);
        CurrentIndex++;
        _images[CurrentIndex].AddRange(images); _media[CurrentIndex].AddRange(media);
        Current.Document.AddRange(strokes);
        Current.Document.RestoreInkLayers(source.Document.InkLayerCount);
        CarryDrawingState(source, Current);
        Current.ViewTransform = source.ViewTransform;
        _papers[Current] = _papers[Current] with { Name = $"{PageName(CurrentIndex - 1)} 副本" };
        int index = CurrentIndex;
        var copy = Current;
        var paper = _papers[copy];
        images = _images[index];
        media = _media[index];
        _pageHistory.Push(new DelegateAction("复制页面",
            () => RestoreDetachedPage(index, copy, paper, images, media, select: true),
            () => DetachPage(_pages.IndexOf(copy))));
        PageChanged?.Invoke(); ImagesChanged?.Invoke();
        return true;
    }

    public void RemoveCurrentPage() => RemovePage(CurrentIndex);

    public bool RemovePage(int index)
    {
        if (index < 0 || index >= PageCount) return false;
        if (_pages.Count == 1)
        {
            Current.Clear();
            Current.Selection.Clear();
            Current.History.Clear();
            _images[0].Clear();
            _media[0].Clear();
            ImagesChanged?.Invoke();
            PageChanged?.Invoke();
            return true;
        }

        var removed = _pages[index];
        var images = _images[index];
        var media = _media[index];
        var paper = _papers[removed];
        bool wasCurrent = ReferenceEquals(Current, removed);
        _pageHistory.Push(new DelegateAction("删除页面", () => DetachPage(_pages.IndexOf(removed)),
            () => RestoreDetachedPage(index, removed, paper, images, media, wasCurrent)));
        DetachPage(index);
        return true;
    }

    private void DetachPage(int index)
    {
        var source = Current;
        var removed = _pages[index];
        removed.CancelLassoErase();
        removed.ClearSelection();
        foreach (var id in _images[index].Select(image => image.Id).Concat(_media[index].Select(item => item.Id)))
            _selectionElements.Remove(id);
        _papers.Remove(removed);
        _pages.RemoveAt(index);
        _images.RemoveAt(index);
        _media.RemoveAt(index);
        CurrentIndex = ReferenceEquals(source, removed) ? Math.Min(index, PageCount - 1) : _pages.IndexOf(source);
        if (ReferenceEquals(source, removed)) CarryDrawingState(source, Current);
        PageStructureVersion++;
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
    }

    private void RestoreDetachedPage(int index, InkController page, BoardPaper paper,
        List<BoardImageAsset> images, List<BoardMediaAsset> media, bool select)
    {
        int insertion = Math.Clamp(index, 0, PageCount);
        var current = Current;
        _pages.Insert(insertion, page);
        _papers.Add(page, paper);
        _images.Insert(insertion, images);
        _media.Insert(insertion, media);
        CurrentIndex = select ? insertion : _pages.IndexOf(current);
        PageStructureVersion++;
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

    /// <summary>
    /// Replace an existing image's raster data and metadata (for example after re-rendering an AI
    /// paste card), keeping its id, position and size. The change is undoable and raises
    /// <see cref="ImagesChanged"/>.
    /// </summary>
    public bool UpdateImage(BoardImageAsset image, byte[] newData, string newMetadata)
    {
        if (image is null || newData is null || newData.Length == 0) return false;
        var pageImages = _images[CurrentIndex];
        var target = pageImages.FirstOrDefault(candidate => candidate.Id == image.Id);
        if (target is null) return false;

        var previousData = target.Data;
        var previousMetadata = target.Metadata ?? "";
        string metadata = newMetadata ?? "";
        target.Data = newData;
        target.Metadata = metadata;

        Current.History.Push(new DelegateAction("update image",
            () => SetImageContent(target, newData, metadata),
            () => SetImageContent(target, previousData, previousMetadata)));
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
    /// Element-level manipulation history mirroring the stroke undo path.
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

    private void ApplyBoard(IReadOnlyList<PreparedPage> loadedPages, int pageIndex = 0)
    {
        foreach (var page in _pages) page.Dispose();
        _pages.Clear();
        _papers.Clear();
        _pageHistory.Clear();
        _nextPageNumber = 1;
        _images.Clear();
        _media.Clear();
        _selectionElements.Clear();
        foreach (var page in loadedPages)
        {
            var controller = new InkController();
            controller.GetSelectableElements = () => GetSelectionElements(controller);
            controller.Document.AddRange(page.Strokes);
            controller.Document.RestoreInkLayers(page.InkLayerCount);
            _pages.Add(controller);
            _papers.Add(controller, page.Paper with { Name = page.Paper.Name ?? $"页面 {_nextPageNumber}" });
            _nextPageNumber++;
            controller.ViewTransform = page.View;
            if (page.Viewport is { Length: 2 } && page.Viewport.All(value => float.IsFinite(value) && value > 0))
                controller.ViewCenter = new(page.Viewport[0] / 2, page.Viewport[1] / 2);
            _images.Add(page.Images);
            _media.Add(page.Media);
        }
        CurrentIndex = Math.Clamp(pageIndex, 0, loadedPages.Count - 1);
        PageStructureVersion++;
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
    }

    private sealed record PreparedPage(List<InkStroke> Strokes, List<BoardImageAsset> Images, List<BoardMediaAsset> Media, BoardPaper Paper, Matrix3x2 View)
    {
        public float[] Viewport { get; init; }
        public int InkLayerCount { get; init; }
    }

    private static PreparedPage PreparePage(BoardPageFile page, BoardPaper fallback)
    {
        var strokes = (page.Strokes ?? new()).Select(FromStrokeFile).ToList();
        ShapeSource.ShareDefinitions(strokes);
        return new(strokes, (page.Images ?? new()).Where(IsValidImage).ToList(),
            (page.Media ?? new()).Where(IsValidMedia).ToList(),
            ReadPaper(page.Background, page.GridSpacing, fallback) with
            { Name = string.IsNullOrWhiteSpace(page.Name) ? null : page.Name.Trim() },
            ReadViewTransform(page.ViewTransform)) { Viewport = page.Viewport, InkLayerCount = page.InkLayerCount };
    }

    private static BoardPaper ReadPaper(string background, float? spacing, BoardPaper fallback) => new(
        Enum.TryParse(background, true, out BoardBackground parsed) && Enum.IsDefined(parsed) ? parsed : fallback.Background,
        spacing is float value && float.IsFinite(value) && value > 0 ? Math.Clamp(value, 12f, 160f) : fallback.GridSpacing);

    private static Matrix3x2 ReadViewTransform(float[] values)
    {
        if (values is not { Length: 6 } || !values.All(float.IsFinite)) return Matrix3x2.Identity;
        var transform = new Matrix3x2(values[0], values[1], values[2], values[3], values[4], values[5]);
        return Matrix3x2.Invert(transform, out _) ? transform : Matrix3x2.Identity;
    }

    private void ReplacePage(int index, PreparedPage page)
    {
        var controller = _pages[index];
        controller.CancelLassoErase(); controller.ClearSelection();
        controller.Document.Clear();
        controller.History.Clear();
        controller.ViewTransform = page.View;
        controller.Document.AddRange(page.Strokes);
        controller.Document.RestoreInkLayers(page.InkLayerCount);
        controller.TrySetActiveInkLayer(0);
        _images[index] = page.Images;
        _media[index] = page.Media;
        _papers[controller] = page.Paper with { Name = page.Paper.Name ?? PageName(index) };
        _pageHistory.Clear();
    }

    private void AddPageAt(int index)
    {
        var paper = _papers[Current];
        var page = new InkController();
        page.GetSelectableElements = () => GetSelectionElements(page);
        _pages.Insert(index, page);
        _papers.Add(page, paper with { Name = NextPageName() });
        _images.Insert(index, new List<BoardImageAsset>());
        _media.Insert(index, new List<BoardMediaAsset>());
        PageStructureVersion++;
    }

    private static bool IsValidImage(BoardImageAsset image)
        => image is not null && image.Data is { Length: > 0 } && image.Width > 0 && image.Height > 0;

    private static bool IsValidMedia(BoardMediaAsset media)
        => media is not null && media.Data is { Length: > 0 } && media.Width > 0 && media.Height > 0
           && media.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    private void SetImageContent(BoardImageAsset image, byte[] data, string metadata)
    {
        image.Data = data;
        image.Metadata = metadata ?? "";
        ImagesChanged?.Invoke();
    }

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
        Id = stroke.Id,
        InkWash = stroke.Attributes.InkWash,
        InkWashSeed = stroke.Attributes.InkWashSeed,
        LayerOrder = stroke.LayerOrder,
        InkLayerIndex = stroke.InkLayerIndex,
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
            InkWash = strokeFile.InkWash.Normalize(),
            InkWashSeed = strokeFile.InkWashSeed,
        };
        var stroke = new InkStroke(attributes) { Id = strokeFile.Id ?? Guid.NewGuid(), IsRecognizedShape = strokeFile.Shape,
            InkLayerIndex = strokeFile.InkLayerIndex, LayerOrder = strokeFile.LayerOrder, ShapeSource = strokeFile.ShapeSource };
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
        [System.Text.Json.Serialization.JsonIgnore]
        public Dictionary<string, byte[]> Extensions { get; set; } = new(StringComparer.Ordinal);
        [System.Text.Json.Serialization.JsonIgnore]
        public BoardArchiveMetadata Archive { get; set; }
        public string Format { get; set; }
        public int Version { get; set; }
        public string Background { get; set; }
        public float GridSpacing { get; set; }
        public List<BoardPageFile> Pages { get; set; } = new();
        public SideBoardFile SideBoard { get; set; }
        public List<BoardBlobReference> Blobs { get; set; } = new();
        public List<StrokeFile> Strokes { get; set; }
    }

    private sealed class BoardPageFile
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int InkLayerCount { get; set; }
        public float[] Viewport { get; set; }
        public string Name { get; set; }
        public float[] ViewTransform { get; set; }
        public string Background { get; set; }
        public float? GridSpacing { get; set; }
        public List<StrokeFile> Strokes { get; set; } = new();
        public List<BoardImageAsset> Images { get; set; } = new();
        public List<BoardMediaAsset> Media { get; set; } = new();
    }

    private sealed class StrokeFile
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int InkLayerIndex { get; set; }
        public Guid? Id { get; set; }
        public InkWashBrushOptions InkWash { get; set; } = InkWashBrushOptions.Default;
        public int InkWashSeed { get; set; }
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
