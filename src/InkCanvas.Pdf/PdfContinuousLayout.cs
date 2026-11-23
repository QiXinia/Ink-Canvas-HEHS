using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Pdf;

/// <summary>Continuous document coordinates with page-local annotation coordinates.</summary>
public sealed class PdfContinuousLayout
{
    private readonly Rect[] _pages;
    private readonly Matrix3x2[] _pageTransforms;
    private readonly (int First, int Last, Rect Bounds)[] _rows;
    private readonly int[] _pageRows;
    public float Width { get; }
    public float Height { get; }
    public int Count => _pages.Length;
    public Rect this[int index] => _pages[index];

    public PdfContinuousLayout(IReadOnlyList<PdfPageGeometry> pages, float gap) : this(pages, gap, false, false, null) { }

    public PdfContinuousLayout(IReadOnlyList<PdfPageGeometry> pages, float gap = 24,
        bool doublePages = false, bool separateCover = false, IReadOnlyList<int> rotations = null)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF must contain at least one page.", nameof(pages));
        _pages = new Rect[pages.Count];
        _pageTransforms = new Matrix3x2[pages.Count];
        _pageRows = new int[pages.Count];
        var sizes = new Vector2[pages.Count];
        for (int index = 0; index < pages.Count; index++)
        {
            var original = pages[index];
            var rotated = original with { Rotation = original.Rotation + (rotations?[index] ?? 0) };
            sizes[index] = new(rotated.DipWidth, rotated.DipHeight);
            _pageTransforms[index] = original.TransformTo(rotated);
        }
        var rows = new List<(int First, int Last, Rect Bounds)>();
        float y = 0;
        for (int index = 0; index < pages.Count;)
        {
            int last = doublePages && !(separateCover && index == 0) ? Math.Min(index + 1, pages.Count - 1) : index;
            float width = sizes[index].X + (last == index ? 0 : gap + sizes[last].X);
            float height = Math.Max(sizes[index].Y, sizes[last].Y);
            rows.Add((index, last, new(0, y, width, y + height)));
            Width = Math.Max(Width, width);
            y += height + gap;
            index = last + 1;
        }
        Height = y - gap;
        _rows = rows.ToArray();
        for (int row = 0; row < _rows.Length; row++)
        {
            var (first, last, bounds) = _rows[row];
            float x = (Width - bounds.Width) / 2;
            _rows[row] = (first, last, new(x, bounds.Top, x + bounds.Width, bounds.Bottom));
            for (int index = first; index <= last; index++)
            {
                _pageRows[index] = row;
                _pages[index] = new(x, bounds.Top, x + sizes[index].X, bounds.Top + sizes[index].Y);
                x += sizes[index].X + gap;
            }
        }
    }

    public Matrix3x2 PageView(int index, Matrix3x2 documentView)
        => _pageTransforms[index] * Matrix3x2.CreateTranslation(_pages[index].Left, _pages[index].Top) * documentView;

    public Matrix3x2 DocumentView(int index, Matrix3x2 pageView)
    {
        Matrix3x2.Invert(PageView(index, Matrix3x2.Identity), out var inverse);
        return inverse * pageView;
    }

    public Rect SpreadBounds(int index) => _rows[_pageRows[index]].Bounds;

    public Rect ToDocumentBounds(int index, Rect bounds)
    {
        var transform = PageView(index, Matrix3x2.Identity);
        var corners = new[] { Vector2.Transform(new(bounds.Left, bounds.Top), transform), Vector2.Transform(new(bounds.Right, bounds.Top), transform),
            Vector2.Transform(new(bounds.Left, bounds.Bottom), transform), Vector2.Transform(new(bounds.Right, bounds.Bottom), transform) };
        return new(corners.Min(point => point.X), corners.Min(point => point.Y), corners.Max(point => point.X), corners.Max(point => point.Y));
    }

    public int AdjacentPage(int index, int direction)
    {
        int row = _pageRows[index] + Math.Sign(direction);
        return row < 0 ? 0 : row >= _rows.Length ? Count - 1 : _rows[row].First;
    }

    public int NearestPage(float y)
    {
        int low = 0, high = _rows.Length - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (y > (_rows[middle].Bounds.Bottom + _rows[middle + 1].Bounds.Top) / 2) low = middle + 1;
            else high = middle;
        }
        return _rows[low].First;
    }

    public int NearestPage(Vector2 point)
    {
        var row = _rows[_pageRows[NearestPage(point.Y)]];
        if (row.First == row.Last) return row.First;
        float first = Vector2.DistanceSquared(point, Vector2.Clamp(point,
            new(_pages[row.First].Left, _pages[row.First].Top), new(_pages[row.First].Right, _pages[row.First].Bottom)));
        float last = Vector2.DistanceSquared(point, Vector2.Clamp(point,
            new(_pages[row.Last].Left, _pages[row.Last].Top), new(_pages[row.Last].Right, _pages[row.Last].Bottom)));
        return first <= last ? row.First : row.Last;
    }

    public int HitTest(Vector2 point)
    {
        int index = NearestPage(point);
        return _pages[index].Contains(point) ? index : -1;
    }

    public IEnumerable<int> VisiblePages(float top, float bottom)
    {
        for (int index = NearestPage(top); index < Count && _pages[index].Top <= bottom; index++)
            if (_pages[index].Bottom >= top) yield return index;
    }

    /// <summary>Visible pages first, followed by bounded look-ahead in the direction of travel.</summary>
    public IEnumerable<int> PreviewPages(float top, float bottom, int direction)
    {
        int first = NearestPage(top), last = _rows[_pageRows[NearestPage(bottom)]].Last;
        foreach (int index in VisiblePages(top, bottom)) yield return index;
        for (int distance = 1; distance <= (direction == 0 ? 1 : 3); distance++)
        {
            int ahead = direction < 0 ? first - distance : last + distance;
            int behind = direction < 0 ? last + distance : first - distance;
            if (ahead >= 0 && ahead < Count) yield return ahead;
            if (distance == 1 && behind >= 0 && behind < Count) yield return behind;
        }
    }
}
