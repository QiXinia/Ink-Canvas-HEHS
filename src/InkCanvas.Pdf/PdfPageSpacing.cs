using System.Numerics;
using InkCanvas.Core.Ink;

namespace InkCanvas.Pdf;

public sealed record PdfColumnGuide(Guid Id, float X);
public sealed record PdfBlankBand(Guid Id, Guid? LeftGuide, Guid? RightGuide, float SourceY, float Height);

/// <summary>Page-local spacing in the original displayed orientation and 96-DPI units.</summary>
public sealed record PdfPageSpacing(PdfColumnGuide[] Guides, PdfBlankBand[] Bands)
{
    public static PdfPageSpacing Empty { get; } = new([], []);
    public const int MaximumGuides = 15;
    public const int MaximumBands = 128;
    public const float MaximumAddedHeight = 16000;

    public bool IsEmpty => Guides.Length == 0 && Bands.Length == 0;

    public bool IsValid(PdfPageGeometry page)
    {
        if (Guides is null || Bands is null || Guides.Length > MaximumGuides || Bands.Length > MaximumBands) return false;
        if (Guides.Any(g => g is null || g.Id == Guid.Empty || !float.IsFinite(g.X) || g.X <= 0 || g.X >= page.DipWidth)
            || Guides.Select(g => g.X).Distinct().Count() != Guides.Length
            || Bands.Any(b => b is null || b.Id == Guid.Empty || !float.IsFinite(b.SourceY) || b.SourceY < 0 || b.SourceY > page.DipHeight
                || !float.IsFinite(b.Height) || b.Height <= 0 || b.Height > MaximumAddedHeight)
            || Guides.Select(g => g.Id).Concat(Bands.Select(b => b.Id)).Distinct().Count() != Guides.Length + Bands.Length) return false;
        var xs = Guides.ToDictionary(guide => guide.Id, guide => guide.X);
        return Bands.All(band => (band.LeftGuide is null || xs.ContainsKey(band.LeftGuide.Value))
            && (band.RightGuide is null || xs.ContainsKey(band.RightGuide.Value))
            && (band.LeftGuide is null || band.RightGuide is null || xs[band.LeftGuide.Value] < xs[band.RightGuide.Value]))
            && Lanes(page).All(lane => lane.AddedHeight <= MaximumAddedHeight);
    }

    public PdfPageSpacing Remove(Guid id) => new(Guides.Where(item => item.Id != id).ToArray(),
        Bands.Where(item => item.Id != id && item.LeftGuide != id && item.RightGuide != id).ToArray());

    public PdfPageGeometry Expand(PdfPageGeometry page)
    {
        if (Bands.Length == 0) return page;
        double delta = Lanes(page).Max(lane => lane.AddedHeight) * (72 / (96 * page.UserUnit));
        return page.NormalizedRotation switch
        {
            90 => page with { Right = page.Right + delta },
            180 => page with { Top = page.Top + delta },
            270 => page with { Left = page.Left - delta },
            _ => page with { Bottom = page.Bottom - delta },
        };
    }

    public PdfSpacingLane[] Lanes(PdfPageGeometry page)
    {
        var edges = Guides.OrderBy(guide => guide.X).ToArray();
        var positions = edges.ToDictionary(guide => guide.Id, guide => guide.X);
        var lanes = new PdfSpacingLane[edges.Length + 1];
        for (int i = 0; i < lanes.Length; i++)
        {
            var left = i > 0 ? edges[i - 1] : null;
            var right = i < edges.Length ? edges[i] : null;
            float x = left?.X ?? 0, end = right?.X ?? page.DipWidth;
            var bands = Bands.Where(band => (band.LeftGuide is null || positions[band.LeftGuide.Value] <= x)
                && (band.RightGuide is null || positions[band.RightGuide.Value] >= end)).OrderBy(band => band.SourceY).ToArray();
            lanes[i] = new(x, end, left?.Id, right?.Id, bands);
        }
        return lanes;
    }

    public Vector2 MapSource(PdfPageGeometry page, Vector2 point)
    {
        var lanes = Lanes(page);
        var lane = lanes.FirstOrDefault(lane => point.X < lane.Right) ?? lanes[^1];
        return point + new Vector2(0, lane.OffsetAt(point.Y));
    }

    public static IReadOnlyList<InkStroke> RemapInk(PdfPageGeometry page, PdfPageSpacing before, PdfPageSpacing after,
        IReadOnlyList<InkStroke> strokes)
    {
        if (strokes.Count == 0 || before.Bands.Length == 0 && after.Bands.Length == 0) return strokes;
        var beforeLanes = before.Lanes(page);
        var afterLanes = after.Lanes(page);
        var edges = before.Guides.Concat(after.Guides).Select(guide => guide.X).Distinct().Order().ToArray();
        var bounds = strokes.Aggregate(Rect.Empty, (result, stroke) => result.Union(stroke.Bounds));
        var slices = new List<(Rect Bounds, float Offset)>();
        var removed = new List<Rect>();
        for (int i = 0; i <= edges.Length; i++)
        {
            float left = i == 0 ? MathF.Min(0, bounds.Left) : edges[i - 1];
            float right = i == edges.Length ? MathF.Max(page.DipWidth, bounds.Right) : MathF.BitDecrement(edges[i]);
            var oldLane = beforeLanes.First(lane => left < lane.Right);
            var newLane = afterLanes.First(lane => left < lane.Right);
            var cuts = oldLane.Bands.Concat(newLane.Bands).Select(band => band.SourceY).Distinct().Order().ToArray();
            float minY = MathF.Min(bounds.Top, 0), maxY = MathF.Max(page.DipHeight + MaximumAddedHeight + 1, bounds.Bottom);
            for (int j = 0; j <= cuts.Length; j++)
            {
                float start = j == 0 ? minY : cuts[j - 1];
                float oldShift = oldLane.OffsetAt(start);
                float end = j == cuts.Length ? maxY : MathF.BitDecrement(cuts[j] + oldShift);
                if (end > start + oldShift)
                    slices.Add((new(left, start + oldShift, right, end), newLane.OffsetAt(start) - oldShift));
            }
            var destinations = newLane.PlacedBands().ToDictionary(item => item.Band.Id, item => item.Top);
            foreach (var (band, top) in oldLane.PlacedBands())
                if (destinations.TryGetValue(band.Id, out float destination))
                    slices.Add((new(left, top, right, MathF.BitDecrement(top + band.Height)), destination - top));
                else removed.Add(new(left, top, right, MathF.BitDecrement(top + band.Height)));
        }

        var remapped = new List<InkStroke>(strokes.Count);
        var cutGroups = new HashSet<Guid>();
        var affected = removed.Concat(slices.Where(slice => slice.Offset != 0).Select(slice => slice.Bounds)).ToArray();
        foreach (var stroke in strokes)
        {
            var extent = stroke.Bounds;
            if (!affected.Any(area => area.Intersects(extent)))
            {
                remapped.Add(stroke);
                continue;
            }
            bool contained = false;
            foreach (var (area, offset) in slices)
            {
                if (area.Contains(new(extent.Left, extent.Top)) && area.Contains(new(extent.Right, extent.Bottom)))
                {
                    remapped.Add(offset == 0 ? stroke : Shift(stroke.Clone(), offset));
                    contained = true;
                    break;
                }
            }
            if (contained) continue;
            foreach (var (area, offset) in slices)
            {
                if (!area.Intersects(extent)) continue;
                foreach (var fragment in PdfInkSplitter.Clip(stroke, area, freezeShape: true))
                {
                    remapped.Add(Shift(fragment, offset));
                }
            }
            if (stroke.ShapeSource?.Geometry?.IsEditable == true)
                cutGroups.Add(stroke.ShapeSource.GroupId);
        }
        for (int index = 0; index < remapped.Count; index++)
        {
            var stroke = remapped[index];
            if (stroke.ShapeSource is not { Geometry.IsEditable: true } source || !cutGroups.Contains(source.GroupId)) continue;
            var copy = stroke.Clone();
            copy.ShapeSource = ShapeSource.FromGeometry(source.GroupId, source.Geometry.AsContours());
            remapped[index] = copy;
        }
        return remapped;

        static InkStroke Shift(InkStroke stroke, float dy)
        {
            if (dy != 0) stroke.Transform(Matrix3x2.CreateTranslation(0, dy), scaleWidth: false);
            return stroke;
        }
    }
}

public sealed record PdfSpacingLane(float Left, float Right, Guid? LeftGuide, Guid? RightGuide, PdfBlankBand[] Bands)
{
    public float AddedHeight => Bands.Sum(band => band.Height);
    public float OffsetAt(float sourceY) => Bands.Where(band => band.SourceY <= sourceY).Sum(band => band.Height);

    public IEnumerable<(PdfBlankBand Band, float Top)> PlacedBands()
    {
        float shift = 0;
        foreach (var band in Bands)
        {
            yield return (band, band.SourceY + shift);
            shift += band.Height;
        }
    }

    public float SourceY(float displayedY)
    {
        float offset = 0;
        foreach (var (band, top) in PlacedBands())
        {
            if (displayedY < top) break;
            if (displayedY < top + band.Height) return band.SourceY;
            offset += band.Height;
        }
        return displayedY - offset;
    }

    public IEnumerable<(Rect Source, float Offset)> ContentSlices(float height)
    {
        float start = 0, offset = 0;
        foreach (var band in Bands)
        {
            if (band.SourceY > start) yield return (new(Left, start, Right, band.SourceY), offset);
            start = band.SourceY;
            offset += band.Height;
        }
        if (start < height) yield return (new(Left, start, Right, height), offset);
    }
}
