using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;

namespace InkCanvas.Ink;

public interface IPolygonExtension
{
    IReadOnlyList<int> PresetVertexCounts { get; }
    Vector2[] RecognizeClosedStroke(InkStroke stroke, float viewScale);
    bool SupportsAdvancedSelectionTransforms => false;
    IReadOnlyList<ShapeKind> AdditionalShapes => Array.Empty<ShapeKind>();
}
