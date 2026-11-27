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
    private readonly int[] _order, _positions;
    public float Width { get; }
    public float Height { get; }
    public int Count => _order.Length;
    public Rect this[int index] => _pages[index];
    public int PageAt(int position) => _order[position];
    public int PositionOf(int page) => (uint)page < (uint)_positions.Length ? _positions[page] : -1;

    public PdfContinuousLayout(IReadOnlyList<PdfPageGeometry> pages, float gap) : this(pages, gap, false, false, null) { }

    public PdfContinuousLayout(IReadOnlyList<PdfPageGeometry> pages, float gap = 24,
        bool doublePages = false, bool separateCover = false, IReadOnlyList<int> rotations = null, IReadOnlyList<int> pageOrder = null)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF must contain at least one page.", nameof(pages));
        _pages = new Rect[pages.Count];
        _pageTransforms = new Matrix3x2[pages.Count];
        _pageRows = new int[pages.Count];
        _order = pageOrder?.ToArray() ?? Enumerable.Range(0, pages.Count).ToArray();
        if (_order.Length == 0) throw new ArgumentException("A PDF must contain at least one page.", nameof(pageOrder));
        _positions = new int[pages.Count];
        Array.Fill(_positions, -1);
        var sizes = new Vector2[pages.Count];
        for (int position = 0; position < Count; position++)
        {
            int index = _order[position];
            if ((uint)index >= (uint)pages.Count || _positions[index] >= 0)
                throw new ArgumentException("Invalid PDF page order.", nameof(pageOrder));
            _positions[index] = position;
            var original = pages[index];
            var rotated = original with { Rotation = original.Rotation + (rotations?[index] ?? 0) };
            sizes[index] = new(rotated.DipWidth, rotated.DipHeight);
            _pageTransforms[index] = original.TransformTo(rotated);
        }
        var rows = new List<(int First, int Last, Rect Bounds)>();
        float y = 0;
        for (int position = 0; position < Count;)
        {
            int last = doublePages && !(separateCover && position == 0) ? Math.Min(position + 1, Count - 1) : position;
            var firstSize = sizes[_order[position]];
            var lastSize = sizes[_order[last]];
            float width = firstSize.X + (last == position ? 0 : gap + lastSize.X);
            float height = Math.Max(firstSize.Y, lastSize.Y);
            rows.Add((position, last, new(0, y, width, y + height)));
            Width = Math.Max(Width, width);
            y += height + gap;
            position = last + 1;
        }
        Height = y - gap;
        _rows = rows.ToArray();
        for (int row = 0; row < _rows.Length; row++)
        {
            var (first, last, bounds) = _rows[row];
            float x = (Width - bounds.Width) / 2;
            _rows[row] = (first, last, new(x, bounds.Top, x + bounds.Width, bounds.Bottom));
            for (int position = first; position <= last; position++)
            {
                int index = _order[position];
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
        // Page transforms at identity document zoom are translations and quarter turns.
        var a = Vector2.Transform(new(bounds.Left, bounds.Top), transform);
        var b = Vector2.Transform(new(bounds.Right, bounds.Bottom), transform);
        return new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    public int AdjacentPage(int index, int direction)
    {
        int row = _pageRows[index] + Math.Sign(direction);
        return _order[row < 0 ? 0 : row >= _rows.Length ? Count - 1 : _rows[row].First];
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
        return _order[_rows[low].First];
    }

    public int NearestPage(Vector2 point)
    {
        var row = _rows[_pageRows[NearestPage(point.Y)]];
        int firstPage = _order[row.First], lastPage = _order[row.Last];
        if (firstPage == lastPage) return firstPage;
        float first = Vector2.DistanceSquared(point, Vector2.Clamp(point,
            new(_pages[firstPage].Left, _pages[firstPage].Top), new(_pages[firstPage].Right, _pages[firstPage].Bottom)));
        float last = Vector2.DistanceSquared(point, Vector2.Clamp(point,
            new(_pages[lastPage].Left, _pages[lastPage].Top), new(_pages[lastPage].Right, _pages[lastPage].Bottom)));
        return first <= last ? firstPage : lastPage;
    }

    public int HitTest(Vector2 point)
    {
        int index = NearestPage(point);
        return _pages[index].Contains(point) ? index : -1;
    }

    public IEnumerable<int> VisiblePages(float top, float bottom)
    {
        for (int position = PositionOf(NearestPage(top)); position < Count; position++)
        {
            int page = _order[position];
            if (_pages[page].Top > bottom) yield break;
            if (_pages[page].Bottom >= top) yield return page;
        }
    }

    /// <summary>Visible pages first, followed by bounded look-ahead in the direction of travel.</summary>
    public IEnumerable<int> PreviewPages(float top, float bottom, int direction)
    {
        int first = PositionOf(NearestPage(top)), last = _rows[_pageRows[NearestPage(bottom)]].Last;
        foreach (int index in VisiblePages(top, bottom)) yield return index;
        for (int distance = 1; distance <= (direction == 0 ? 1 : 3); distance++)
        {
            int ahead = direction < 0 ? first - distance : last + distance;
            int behind = direction < 0 ? last + distance : first - distance;
            if (ahead >= 0 && ahead < Count) yield return _order[ahead];
            if (distance == 1 && behind >= 0 && behind < Count) yield return _order[behind];
        }
    }
}
