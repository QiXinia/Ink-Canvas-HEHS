using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Ink;
using System.Numerics;
using System.Text.Json;

namespace InkCanvas.Features.Board;

public enum BoardBackground { Black, White, Grid, Lined, Green, Cream, Coordinate }

public sealed record BoardPageInfo(InkController Controller, BoardBackground Background, float GridSpacing,
    IReadOnlyList<BoardImageAsset> Images, IReadOnlyList<BoardMediaAsset> Media);

/// <summary>
/// AI 板书对象变化：供课堂录制器写 <c>TimelineEventKind.AiStep</c>，与 <c>ImagesChanged</c>
/// 分离，避免普通图片变化污染时光机对象层。
/// </summary>
public sealed class BoardAiObjectChange
{
    /// <summary>add / replace / update / remove。</summary>
    public string Action { get; init; } = "";

    public int PageIndex { get; init; }

    /// <summary>本次变化涉及的对象快照；remove 时用于保留被删除对象。</summary>
    public IReadOnlyList<BoardImageAsset> Objects { get; init; } = Array.Empty<BoardImageAsset>();

    /// <summary>replace 时被删除的笔画；录制器据此补写既有 StrokesRemoved 事件。</summary>
    public IReadOnlyList<InkStroke> RemovedStrokes { get; init; } = Array.Empty<InkStroke>();
}

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

    /// <summary>Optional camelCase JSON payload (for example AI paste source); empty for plain images.</summary>
    public string Metadata { get; set; } = "";

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
        Metadata = Metadata ?? "",
        Opacity = Opacity,
        Transform = Transform?.ToArray(),
    };
}

public readonly record struct BoardImageGeometry(float Left, float Top, float Width, float Height);

/// <summary>Lightweight page summary for thumbnail/list UIs (no bitmap rendering required).</summary>
public readonly record struct BoardPageThumbnail(
    int Index, string Name, BoardBackground Background, int StrokeCount, int ImageCount);
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
/// Multi-page blackboard/whiteboard. Every page owns its ink controller, undo history, image
/// collection and <see cref="BoardPageState"/> (name, background, grid spacing, view transform).
/// Coordinates and sizes in both collections are stored in WinUI device-independent pixels,
/// not physical screen pixels.
/// </summary>
public sealed partial class BoardService
{
    private const string BoardFormat = "inkcanvas-board";
    private const int BoardFormatVersion = 4;
    private const int SideBoardFormatVersion = 1;
    public const int MaximumPages = 99;
    private const float MinimumGridSpacing = 12f;
    private const float MaximumGridSpacing = 160f;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<InkController> _pages = new();
    private sealed record BoardPaper(BoardBackground Background = BoardBackground.Black, float GridSpacing = 40f);
    private readonly Dictionary<InkController, BoardPaper> _papers = new();
    private readonly Dictionary<Guid, BoardSelectionElement> _selectionElements = new();
    private readonly List<List<BoardImageAsset>> _images = new() { new List<BoardImageAsset>() };
    private readonly List<BoardPageState> _states = new() { new BoardPageState { Name = "页面 1" } };

    /// <summary>Page-level undo/redo for structural operations (insert, copy, move, rename, delete).</summary>
    private readonly UndoStack _pageHistory = new(100);
    private readonly List<List<BoardMediaAsset>> _media = new() { new List<BoardMediaAsset>() };

    public BoardService(InkController initialController = null)
    {
        var page = initialController ?? new InkController();
        _pages.Add(page);
        _papers.Add(page, new());
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

    /// <summary>Background of the current page. Kept for backward compatibility with callers.</summary>
    public BoardBackground Background
    {
        get => CurrentState.Background;
        set => SetBackground(value);
    }

    /// <summary>Grid/lined/coordinate spacing of the current page, in device-independent pixels.</summary>
    public float GridSpacing
    {
        get => CurrentState.GridSpacing;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            value = Math.Clamp(value, MinimumGridSpacing, MaximumGridSpacing);
            if (CurrentState.GridSpacing == value) return;
            CurrentState.GridSpacing = value;
            SyncPaper(CurrentIndex);
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

    /// <summary>Page creation is limited only by the runtime collection capacity; <see cref="MaximumPages"/> is a UI soft limit.</summary>
    public bool CanAddPages(int count = 1) => count > 0 && count <= Array.MaxLength - PageCount;

    public bool HasContent => _pages.Any(page => page.Document.Count > 0)
        || _images.Any(images => images.Count > 0)
        || _media.Any(media => media.Count > 0);

    /// <summary>当前页是否有可渲染贴回的内容（有墨迹，或含有效数据的图片；视频不参与离线 PNG 合成）。</summary>
    public bool CurrentPageHasPasteableContent => Current.Document.Count > 0
        || _images[CurrentIndex].Any(image => image.Data is { Length: > 0 });

    public InkController Current => _pages[CurrentIndex];

    public IReadOnlyList<InkController> Pages => _pages;

    /// <summary>与主展板一起保存/加载的板中板服务；为 null 时主文件不写 SideBoard 节点。</summary>
    public BoardService SideBoardSource { get; set; }

    /// <summary>Visual state (name/background/grid/view) of the current page.</summary>
    public BoardPageState CurrentState => _states[CurrentIndex];


    public IReadOnlyList<BoardImageAsset> CurrentImages => _images[CurrentIndex];

    public bool CanUndoPage => _pageHistory.CanUndo;

    public bool CanRedoPage => _pageHistory.CanRedo;
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

    /// <summary>AI 板书对象增删改（AI 卡片带 <see cref="AiPasteMetadata"/>）；普通图片不触发。</summary>
    public event Action<BoardAiObjectChange> AiObjectsChanged;

    public bool UndoPage() => _pageHistory.Undo();

    public bool RedoPage() => _pageHistory.Redo();

    public void SetBackground(BoardBackground background)
    {
        if (!Enum.IsDefined(background)) throw new ArgumentOutOfRangeException(nameof(background));
        if (CurrentState.Background == background) return;
        CurrentState.Background = background;
        SyncPaper(CurrentIndex);
        BackgroundChanged?.Invoke();
    }

    public void NextPage()
    {
        if (CurrentIndex < _pages.Count - 1)
        {
            GoTo(CurrentIndex + 1);
            return;
        }
        if (!CanAddPages()) return;
        SaveCurrentViewState();
        var source = Current;
        AddPageAt(_pages.Count);
        CurrentIndex = _pages.Count - 1;
        CarryDrawingState(source, Current);
        ApplyCurrentViewState();
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
        SaveCurrentViewState();
        var source = Current;
        CurrentIndex = index;
        CarryDrawingState(source, Current);
        ApplyCurrentViewState();
        PageChanged?.Invoke();
    }

    public bool InsertPageAfterCurrent()
    {
        if (!CanAddPages()) return false;
        SaveCurrentViewState();
        var source = Current;
        AddPageAt(CurrentIndex + 1);
        CurrentIndex++;
        CarryDrawingState(source, Current);
        ApplyCurrentViewState();
        PageChanged?.Invoke();
        return true;
    }

    /// <summary>Deep-copy the current page (strokes, images and visual state) into a page right after it.</summary>
    public bool CopyCurrentPage()
    {
        if (!CanAddPages()) return false;
        SaveCurrentViewState();

        int source = CurrentIndex;
        var sourceState = _states[source];

        var controller = new InkController();
        var strokeCopies = _pages[source].Document.Strokes
            .Select(stroke => FromStrokeFile(ToStrokeFile(stroke)))
            .ToList();
        controller.Document.AddRange(strokeCopies);
        controller.ViewTransform = sourceState.ViewTransform;

        var state = new BoardPageState
        {
            Name = $"{sourceState.Name} 副本",
            Background = sourceState.Background,
            GridSpacing = sourceState.GridSpacing,
            ViewTransform = sourceState.ViewTransform,
        };
        var images = _images[source].Select(image => image.Copy()).ToList();
        var media = _media[source].Select(item => item.Copy(preserveId: false, copyData: false)).ToList();
        var paper = new BoardPaper(sourceState.Background, sourceState.GridSpacing);

        int insertAt = source + 1;
        controller.GetSelectableElements = () => GetSelectionElements(controller);
        _pages.Insert(insertAt, controller);
        _images.Insert(insertAt, images);
        _media.Insert(insertAt, media);
        _states.Insert(insertAt, state);
        _papers[controller] = paper;
        CurrentIndex = insertAt;
        ApplyCurrentViewState();

        _pageHistory.Push(new DelegateAction("复制页面",
            () =>
            {
                _pages.Insert(insertAt, controller);
                _images.Insert(insertAt, images);
                _media.Insert(insertAt, media);
                _states.Insert(insertAt, state);
                _papers[controller] = paper;
                CurrentIndex = insertAt;
                ApplyCurrentViewState();
                PageChanged?.Invoke();
                ImagesChanged?.Invoke();
            },
            () =>
            {
                int at = _pages.IndexOf(controller);
                if (at < 0) return;
                _pages.RemoveAt(at);
                _images.RemoveAt(at);
                _media.RemoveAt(at);
                _states.RemoveAt(at);
                _papers.Remove(controller);
                if (at < CurrentIndex) CurrentIndex--;
                else if (at == CurrentIndex) CurrentIndex = Math.Min(at, _pages.Count - 1);
                ApplyCurrentViewState();
                PageChanged?.Invoke();
                ImagesChanged?.Invoke();
            }));

        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
        return true;
    }

    /// <summary>Move a page to a new position. <paramref name="toIndex"/> is the final index.</summary>
    public bool MovePage(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _pages.Count) return false;
        toIndex = Math.Clamp(toIndex, 0, _pages.Count - 1);
        if (fromIndex == toIndex) return false;

        SaveCurrentViewState();
        var currentPage = _pages[CurrentIndex];
        MovePageCore(fromIndex, toIndex);
        CurrentIndex = _pages.IndexOf(currentPage);
        ApplyCurrentViewState();

        _pageHistory.Push(new DelegateAction("移动页面",
            () =>
            {
                MovePageCore(fromIndex, toIndex);
                CurrentIndex = Math.Clamp(CurrentIndex, 0, _pages.Count - 1);
                ApplyCurrentViewState();
                PageChanged?.Invoke();
            },
            () =>
            {
                MovePageCore(toIndex, fromIndex);
                CurrentIndex = Math.Clamp(CurrentIndex, 0, _pages.Count - 1);
                ApplyCurrentViewState();
                PageChanged?.Invoke();
            }));

        PageChanged?.Invoke();
        return true;
    }

    /// <summary>Rename a page. Empty/whitespace names are rejected.</summary>
    public bool RenamePage(int index, string name)
    {
        if (index < 0 || index >= _states.Count) return false;
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return false;
        var oldName = _states[index].Name;
        if (string.Equals(oldName, trimmed, StringComparison.Ordinal)) return false;

        _states[index].Name = trimmed;
        _pageHistory.Push(new DelegateAction("重命名页面",
            () => { _states[index].Name = trimmed; PageChanged?.Invoke(); },
            () => { _states[index].Name = oldName; PageChanged?.Invoke(); }));
        PageChanged?.Invoke();
        return true;
    }

    public string PageName(int index)
        => index >= 0 && index < _states.Count ? _states[index].Name : string.Empty;

    public BoardBackground PageBackground(int index)
        => index >= 0 && index < _states.Count ? _states[index].Background : BoardBackground.Black;

    public float PageGridSpacing(int index)
        => index >= 0 && index < _states.Count ? _states[index].GridSpacing : 0f;

    /// <summary>Metadata for one page, suitable for a text/colour thumbnail in the page list.</summary>
    public BoardPageThumbnail GetPageThumbnail(int index)
    {
        if (index < 0 || index >= _pages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new BoardPageThumbnail(
            index, _states[index].Name, _states[index].Background,
            _pages[index].Document.Count, _images[index].Count);
    }

    public IReadOnlyList<BoardPageThumbnail> GetPageThumbnails()
        => Enumerable.Range(0, _pages.Count).Select(GetPageThumbnail).ToList();

    /// <summary>Transfer prepared pages in one UI update. A rejected batch leaves all pages intact.</summary>
    public bool InsertPagesAfterCurrent(IReadOnlyList<(InkController Ink, IReadOnlyList<BoardImageAsset> Images)> pages)
    {
        if (pages is null) return false;
        if (pages.Any(page => page.Ink is null || _pages.Contains(page.Ink) || page.Images is null || page.Images.Any(image => !IsValidImage(image)))
            || pages.Select(page => page.Ink).Distinct().Count() != pages.Count) throw new ArgumentException("Imported pages must have independent controllers and valid images.", nameof(pages));
        if (!CanAddPages(pages.Count)) return false;
        int insertion = CurrentIndex + 1;
        var paper = _papers[Current];
        int insertAt = insertion;
        foreach (var page in pages)
        {
            var controller = page.Ink;
            _pages.Insert(insertAt, controller);
            _images.Insert(insertAt, page.Images.ToList());
            _media.Insert(insertAt, new List<BoardMediaAsset>());
            _states.Insert(insertAt, new BoardPageState
            {
                Name = NextPageName(),
                Background = paper.Background,
                GridSpacing = paper.GridSpacing,
                ViewTransform = controller.ViewTransform,
            });
            _papers.Add(controller, paper);
            controller.GetSelectableElements = () => GetSelectionElements(controller);
            insertAt++;
        }
        PageStructureVersion++;
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
        int pageIndex = CurrentIndex;
        var strokes = page.Document.Strokes.ToList(); var images = pageImages.ToList(); var media = pageMedia.ToList();
        BoardImageAsset[] aiImages = images.Where(IsAiBoardImage).ToArray();
        if (strokes.Count + images.Count + media.Count == 0) return;
        Apply();
        page.History.Push(new DelegateAction("clear board page", Apply, Revert));
        void Apply()
        {
            page.Document.Clear(); pageImages.Clear(); pageMedia.Clear(); ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, aiImages);
        }
        void Revert()
        {
            page.Document.AddRange(strokes); pageImages.AddRange(images); pageMedia.AddRange(media); ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("add", pageIndex, aiImages);
        }
    }


    public bool DuplicateCurrentPage()
    {
        if (!CanAddPages()) return false;
        var source = Current;
        var strokes = InkStroke.CloneObjects(source.Document.Strokes);
        var images = CurrentImages.Select(image => image.Copy(preserveId: false, copyData: false)).ToList();
        var media = CurrentMedia.Select(item => item.Copy(preserveId: false, copyData: false)).ToList();
        AddPageAt(CurrentIndex + 1);
        CurrentIndex++;
        _images[CurrentIndex].AddRange(images); _media[CurrentIndex].AddRange(media);
        Current.Document.AddRange(strokes);
        CarryDrawingState(source, Current);
        Current.ViewTransform = source.ViewTransform;
        _states[CurrentIndex].ViewTransform = source.ViewTransform;
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
            _papers[Current] = new BoardPaper(_states[0].Background, _states[0].GridSpacing);
            ImagesChanged?.Invoke();
            PageChanged?.Invoke();
            return true;
        }

        SaveCurrentViewState();

        var controller = _pages[index];
        var images = _images[index];
        var media = _media[index];
        var state = _states[index];
        var paper = _papers.TryGetValue(controller, out var existingPaper) ? existingPaper : new BoardPaper(state.Background, state.GridSpacing);
        foreach (var id in images.Select(image => image.Id).Concat(media.Select(item => item.Id))) _selectionElements.Remove(id);

        _pages.RemoveAt(index);
        _images.RemoveAt(index);
        _media.RemoveAt(index);
        _states.RemoveAt(index);
        _papers.Remove(controller);
        if (index < CurrentIndex) CurrentIndex--;
        else if (index == CurrentIndex) CurrentIndex = Math.Min(index, _pages.Count - 1);
        ApplyCurrentViewState();
        PageStructureVersion++;

        // Do not dispose the removed controller: undo needs to reattach the exact same instance.
        _pageHistory.Push(new DelegateAction("删除页面",
            () =>
            {
                int at = _pages.IndexOf(controller);
                if (at < 0) return;
                _pages.RemoveAt(at);
                _images.RemoveAt(at);
                _media.RemoveAt(at);
                _states.RemoveAt(at);
                _papers.Remove(controller);
                if (at < CurrentIndex) CurrentIndex--;
                else if (at == CurrentIndex) CurrentIndex = Math.Min(at, _pages.Count - 1);
                ApplyCurrentViewState();
                PageStructureVersion++;
                PageChanged?.Invoke();
                ImagesChanged?.Invoke();
            },
            () =>
            {
                _pages.Insert(index, controller);
                _images.Insert(index, images);
                _media.Insert(index, media);
                _states.Insert(index, state);
                _papers[controller] = paper;
                CurrentIndex = index;
                ApplyCurrentViewState();
                PageStructureVersion++;
                PageChanged?.Invoke();
                ImagesChanged?.Invoke();
            }));
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
        return true;
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
    /// 一次插入多张图片并只记录一个撤销步骤（AI 板书整体写入）。
    /// Undo 移除全部、Redo 原顺序插回；返回首次插入的页内索引。
    /// </summary>
    public IReadOnlyList<int> AddImagesAtomic(IReadOnlyList<BoardImageAsset> images, string actionName)
    {
        if (images is null || images.Count == 0) return Array.Empty<int>();

        var inserted = images.Where(IsValidImage).ToArray();
        if (inserted.Length == 0) return Array.Empty<int>();

        var pageImages = _images[CurrentIndex];
        int pageIndex = CurrentIndex;
        int start = pageImages.Count;
        long layerOrder = Current.NextLayerOrder();
        for (int index = 0; index < inserted.Length; index++)
        {
            inserted[index].LayerOrder = layerOrder + index;
            pageImages.Add(inserted[index]);
        }

        string name = string.IsNullOrWhiteSpace(actionName) ? "AI 板书" : actionName.Trim();
        void ApplyInsert()
        {
            int at = Math.Clamp(start, 0, pageImages.Count);
            pageImages.InsertRange(at, inserted);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("add", pageIndex, inserted);
        }

        void RevertInsert()
        {
            foreach (BoardImageAsset image in inserted) pageImages.Remove(image);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, inserted);
        }

        Current.History.Push(new DelegateAction(name, ApplyInsert, RevertInsert));
        ImagesChanged?.Invoke();
        RaiseAiObjectsChanged("add", pageIndex, inserted);
        return Enumerable.Range(start, inserted.Length).ToArray();
    }

    /// <summary>
    /// 用图片原子替换原笔画：一次 <see cref="DelegateAction"/> 内删除 <paramref name="strokesToRemove"/>
    /// 并插入 <paramref name="images"/>。传空/null 笔画列表表示删除当前页全部笔画（“替换整页”语义）；
    /// 图片为空或页面没有可删除笔画时返回 <c>false</c>，不会只删不写或只写不删。
    /// Undo 移除新图片并按原索引恢复笔画，Redo 反向，均只占一步。
    /// </summary>
    public bool ReplaceStrokesWithImagesAtomic(
        IReadOnlyList<InkStroke> strokesToRemove,
        IReadOnlyList<BoardImageAsset> images,
        string actionName)
    {
        if (images is null || images.Count == 0) return false;

        var inserted = images.Where(IsValidImage).ToArray();
        if (inserted.Length == 0) return false;

        var page = Current;
        var pageImages = _images[CurrentIndex];
        int pageIndex = CurrentIndex;
        var document = page.Document;

        InkStroke[] removed = strokesToRemove is { Count: > 0 }
            ? strokesToRemove
                .Where(stroke => stroke is not null && document.Strokes.Contains(stroke))
                .Distinct()
                .ToArray()
            : document.Strokes.ToArray();

        if (removed.Length == 0) return false;

        var removedSet = new HashSet<InkStroke>(removed);
        (int Index, InkStroke Stroke)[] indexed = document.Strokes
            .Select((stroke, index) => (Index: index, Stroke: stroke))
            .Where(item => removedSet.Contains(item.Stroke))
            .ToArray();

        int start = pageImages.Count;
        long layerOrder = page.NextLayerOrder();
        for (int index = 0; index < inserted.Length; index++)
            inserted[index].LayerOrder = layerOrder + index;

        void Apply()
        {
            document.RemoveAll(removed);
            int at = Math.Clamp(start, 0, pageImages.Count);
            pageImages.InsertRange(at, inserted);
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("replace", pageIndex, inserted, removed);
        }

        void Revert()
        {
            foreach (BoardImageAsset image in inserted) pageImages.Remove(image);
            document.InsertRange(indexed.Select(item => (item.Index, item.Stroke)));
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", pageIndex, inserted);
        }

        Apply();
        page.ClearSelection();

        string name = string.IsNullOrWhiteSpace(actionName) ? "AI 板书替换" : actionName.Trim();
        page.History.Push(new DelegateAction(name, Apply, Revert));
        return true;
    }

    /// <summary>
    /// 当前页内容包围盒（笔画 + 图片 + 媒体，世界坐标），语义与
    /// <c>SideBoardWindow.CurrentPageContentBounds</c> 一致；图片/媒体按轴对齐矩形计，
    /// 不含旋转后的精确外接框。空页返回 <see cref="Rect.Empty"/>。
    /// </summary>
    public Rect GetContentBounds() => GetContentBoundsExcept(null);

    /// <summary>
    /// 同 <see cref="GetContentBounds"/>，但忽略 <paramref name="excludedStrokes"/>（替换原板书前
    /// 用“将被删除的笔画”之外的内容计算自动落点，避免旧笔迹占位导致误判空间不足）。
    /// </summary>
    public Rect GetContentBoundsExcept(IReadOnlyCollection<InkStroke> excludedStrokes)
    {
        HashSet<InkStroke> excluded = excludedStrokes is { Count: > 0 }
            ? new HashSet<InkStroke>(excludedStrokes)
            : null;

        Rect bounds = Rect.Empty;
        foreach (InkStroke stroke in Current.Document.Strokes)
        {
            if (excluded is not null && excluded.Contains(stroke)) continue;
            bounds = bounds.Union(stroke.Bounds);
        }

        foreach (BoardImageAsset image in _images[CurrentIndex])
        {
            if (image.Data is not { Length: > 0 } || image.Width <= 0 || image.Height <= 0) continue;
            bounds = bounds.Union(new Rect(image.Left, image.Top, image.Left + image.Width, image.Top + image.Height));
        }

        foreach (BoardMediaAsset media in _media[CurrentIndex])
        {
            if (media.Width <= 0 || media.Height <= 0) continue;
            bounds = bounds.Union(new Rect(media.Left, media.Top, media.Left + media.Width, media.Top + media.Height));
        }

        return bounds;
    }

    public void RemoveImage(BoardImageAsset image)
    {
        if (image is null) return;
        var pageImages = _images[CurrentIndex];
        int index = pageImages.IndexOf(image);
        if (index < 0) return;

        int pageIndex = CurrentIndex;
        bool ai = IsAiBoardImage(image);
        pageImages.RemoveAt(index);
        Current.History.Push(new DelegateAction("delete image",
            () =>
            {
                pageImages.Remove(image);
                ImagesChanged?.Invoke();
                if (ai) RaiseAiObjectsChanged("remove", pageIndex, new[] { image });
            },
            () =>
            {
                pageImages.Insert(Math.Clamp(index, 0, pageImages.Count), image);
                ImagesChanged?.Invoke();
                if (ai) RaiseAiObjectsChanged("add", pageIndex, new[] { image });
            }));
        ImagesChanged?.Invoke();
        if (ai) RaiseAiObjectsChanged("remove", pageIndex, new[] { image });
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
        int pageIndex = CurrentIndex;
        bool ai = IsAiBoardImage(target);
        string metadata = newMetadata ?? "";
        target.Data = newData;
        target.Metadata = metadata;

        Current.History.Push(new DelegateAction("update image",
            () =>
            {
                SetImageContent(target, newData, metadata);
                if (ai) RaiseAiObjectsChanged("update", pageIndex, new[] { target });
            },
            () =>
            {
                SetImageContent(target, previousData, previousMetadata);
                if (ai) RaiseAiObjectsChanged("update", pageIndex, new[] { target });
            }));
        ImagesChanged?.Invoke();
        if (ai) RaiseAiObjectsChanged("update", pageIndex, new[] { target });
        return true;
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

    private void ApplyBoard(IReadOnlyList<PreparedPage> loadedPages, int currentIndex = 0)
    {
        foreach (var page in _pages) page.Dispose();
        _pages.Clear();
        _papers.Clear();
        _images.Clear();
        _media.Clear();
        _states.Clear();
        _selectionElements.Clear();
        foreach (var page in loadedPages)
        {
            var controller = new InkController();
            var viewTransform = FromTransformArray(page.ViewTransform);
            controller.GetSelectableElements = () => GetSelectionElements(controller);
            controller.Document.AddRange(page.Strokes);
            controller.ViewTransform = viewTransform;
            _pages.Add(controller);
            _papers.Add(controller, page.Paper);
            _images.Add(page.Images);
            _media.Add(page.Media);
            _states.Add(new BoardPageState
            {
                Name = string.IsNullOrWhiteSpace(page.Name) ? $"页面 {_states.Count + 1}" : page.Name,
                Background = page.Paper.Background,
                GridSpacing = page.Paper.GridSpacing,
                ViewTransform = viewTransform,
            });
        }
        CurrentIndex = loadedPages.Count == 0 ? 0 : Math.Clamp(currentIndex, 0, loadedPages.Count - 1);
        PageStructureVersion++;
        PageChanged?.Invoke();
        ImagesChanged?.Invoke();
    }

    private sealed record PreparedPage(List<InkStroke> Strokes, List<BoardImageAsset> Images, List<BoardMediaAsset> Media, BoardPaper Paper,
        string Name = null, float[] ViewTransform = null);

    private static PreparedPage PreparePage(BoardPageFile page, BoardPaper fallback)
    {
        var strokes = (page.Strokes ?? new()).Select(FromStrokeFile).ToList();
        ShapeSource.ShareDefinitions(strokes);
        return new(strokes, (page.Images ?? new()).Where(IsValidImage).ToList(),
            (page.Media ?? new()).Where(IsValidMedia).ToList(), ReadPaper(page.Background, page.GridSpacing, fallback),
            page.Name, page.ViewTransform);
    }

    private static BoardPaper ReadPaper(string background, float? spacing, BoardPaper fallback) => new(
        Enum.TryParse(background, true, out BoardBackground parsed) && Enum.IsDefined(parsed) ? parsed : fallback.Background,
        spacing is float value && float.IsFinite(value) && value > 0 ? Math.Clamp(value, 12f, 160f) : fallback.GridSpacing);

    private void ReplacePage(int index, PreparedPage page)
    {
        var controller = _pages[index];
        controller.CancelLassoErase(); controller.ClearSelection();
        controller.Document.Clear();
        controller.History.Clear();
        controller.Selection.Clear();
        var viewTransform = FromTransformArray(page.ViewTransform);
        controller.ViewTransform = viewTransform;
        controller.Document.AddRange(page.Strokes);
        _images[index] = page.Images;
        _media[index] = page.Media;
        _papers[controller] = page.Paper;

        if (!string.IsNullOrWhiteSpace(page.Name)) _states[index].Name = page.Name;
        _states[index].Background = page.Paper.Background;
        _states[index].GridSpacing = page.Paper.GridSpacing;
        _states[index].ViewTransform = viewTransform;
    }

    private void AddPageAt(int index)
    {

        var basis = _states[Math.Clamp(CurrentIndex, 0, _states.Count - 1)];
        var page = new InkController();
        page.GetSelectableElements = () => GetSelectionElements(page);
        _pages.Insert(index, page);
        _images.Insert(index, new List<BoardImageAsset>());
        _media.Insert(index, new List<BoardMediaAsset>());
        _states.Insert(index, new BoardPageState
        {
            Name = NextPageName(),
            Background = basis.Background,
            GridSpacing = basis.GridSpacing,
            ViewTransform = Matrix3x2.Identity,
        });
        _papers.Add(page, new BoardPaper(basis.Background, basis.GridSpacing));
        PageStructureVersion++;
    }

    private void MovePageCore(int fromIndex, int toIndex)
    {
        var page = _pages[fromIndex];
        _pages.RemoveAt(fromIndex);
        _pages.Insert(toIndex, page);

        var images = _images[fromIndex];
        _images.RemoveAt(fromIndex);
        _images.Insert(toIndex, images);

        var media = _media[fromIndex];
        _media.RemoveAt(fromIndex);
        _media.Insert(toIndex, media);

        var state = _states[fromIndex];
        _states.RemoveAt(fromIndex);
        _states.Insert(toIndex, state);
        PageStructureVersion++;
    }

    private string NextPageName()
    {
        int number = _states.Count + 1;
        string name = $"页面 {number}";
        while (_states.Any(state => string.Equals(state.Name, name, StringComparison.Ordinal)))
        {
            number++;
            name = $"页面 {number}";
        }
        return name;
    }

    private void SyncPaper(int index)
    {
        if (index < 0 || index >= _pages.Count || index >= _states.Count) return;
        _papers[_pages[index]] = new BoardPaper(_states[index].Background, _states[index].GridSpacing);
    }

    private void SaveCurrentViewState()
    {
        if (CurrentIndex >= 0 && CurrentIndex < _states.Count && CurrentIndex < _pages.Count)
            _states[CurrentIndex].ViewTransform = _pages[CurrentIndex].ViewTransform;
    }

    private void ApplyCurrentViewState()
    {
        if (CurrentIndex >= 0 && CurrentIndex < _states.Count && CurrentIndex < _pages.Count)
            _pages[CurrentIndex].ViewTransform = _states[CurrentIndex].ViewTransform;
    }

    private static float[] ToTransformArray(Matrix3x2 transform)
        => new[] { transform.M11, transform.M12, transform.M21, transform.M22, transform.M31, transform.M32 };

    private static Matrix3x2 FromTransformArray(float[] values)
    {
        if (values is null || values.Length < 6) return Matrix3x2.Identity;
        return new Matrix3x2(values[0], values[1], values[2], values[3], values[4], values[5]);
    }

    private static bool IsValidImage(BoardImageAsset image)
        => image is not null && image.Data is { Length: > 0 } && image.Width > 0 && image.Height > 0;

    private void SetImageContent(BoardImageAsset image, byte[] data, string metadata)
    {
        image.Data = data;
        image.Metadata = metadata ?? "";
        ImagesChanged?.Invoke();
    }

    /// <summary>AI 卡片判定：metadata 必须是 <see cref="AiPasteMetadata"/> 风格且包含 kind 字段。</summary>
    private static bool IsAiBoardImage(BoardImageAsset image)
    {
        if (image is null || string.IsNullOrWhiteSpace(image.Metadata)) return false;
        if (image.Metadata.IndexOf("\"kind\"", StringComparison.OrdinalIgnoreCase) < 0) return false;
        return AiPasteMetadata.TryParse(image.Metadata) is not null;
    }

    private void RaiseAiObjectsChanged(string action, int pageIndex,
        IReadOnlyList<BoardImageAsset> objects, IReadOnlyList<InkStroke> removedStrokes = null)
    {
        if (objects is null || objects.Count == 0) return;
        AiObjectsChanged?.Invoke(new BoardAiObjectChange
        {
            Action = action,
            PageIndex = pageIndex,
            Objects = objects.ToArray(),
            RemovedStrokes = removedStrokes ?? Array.Empty<InkStroke>(),
        });
    }
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
        Id = stroke.Id,
        InkWash = stroke.Attributes.InkWash,
        InkWashSeed = stroke.Attributes.InkWashSeed,
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
            InkWash = strokeFile.InkWash.Normalize(),
            InkWashSeed = strokeFile.InkWashSeed,
        };
        var stroke = new InkStroke(attributes) { Id = strokeFile.Id ?? Guid.NewGuid(), IsRecognizedShape = strokeFile.Shape, LayerOrder = strokeFile.LayerOrder, ShapeSource = strokeFile.ShapeSource };
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
        public BoardSideFile SideBoard { get; set; }
        public List<BoardBlobReference> Blobs { get; set; } = new();
        public List<StrokeFile> Strokes { get; set; }
    }

    private sealed class BoardPageFile
    {
        public string Name { get; set; }
        public string Background { get; set; }
        public float? GridSpacing { get; set; }
        public float[] ViewTransform { get; set; }
        public List<StrokeFile> Strokes { get; set; } = new();
        public List<BoardImageAsset> Images { get; set; } = new();
        public List<BoardMediaAsset> Media { get; set; } = new();
    }

    private sealed class BoardSideFile
    {
        public int Version { get; set; }
        public int PageIndex { get; set; }
        public List<BoardPageFile> Pages { get; set; } = new();
    }

    private sealed class StrokeFile
    {
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
