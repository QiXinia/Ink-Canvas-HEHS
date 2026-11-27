using Clipper2Lib;

namespace InkCanvas.Core.Ink;

/// <summary>Converts closed fill rings to and from the integer coordinates used for clipping.</summary>
public static class FilledInkGeometry
{
    public static Paths64 ToPaths(InkStroke stroke, double scale)
    {
        var paths = new Paths64();
        foreach (var (start, count) in stroke.FillContours())
        {
            var path = new Path64(count);
            for (int i = start; i < start + count; i++)
            {
                var point = stroke.Points[i];
                path.Add(new Point64(point.X * scale, point.Y * scale));
            }
            paths.Add(path);
        }
        return paths;
    }

    public static InkStroke FromPaths(InkStroke original, Paths64 paths, double scale)
    {
        var source = original.ShapeSource;
        var sample = original.Points[0];
        var shape = new InkStroke(original.Attributes.Clone())
        {
            InkLayerIndex = original.InkLayerIndex,
            LayerOrder = original.LayerOrder,
            IsRecognizedShape = original.IsRecognizedShape,
            ShapeSource = ShapeSource.FromGeometry(source.GroupId, source.Geometry.AsContours(paths.Select(path => path.Count).ToArray())),
        };
        shape.RestorePoints(paths.SelectMany(path => path).Select(point => sample with
        {
            X = (float)(point.X / scale), Y = (float)(point.Y / scale),
        }));
        shape.Complete();
        return shape;
    }
}
