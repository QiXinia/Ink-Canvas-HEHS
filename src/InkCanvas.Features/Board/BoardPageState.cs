using System.Numerics;

namespace InkCanvas.Features.Board;

/// <summary>
/// Per-page visual state kept in parallel with that page's ink controller. Switching pages
/// stores the outgoing page's current view transform here and applies the incoming page's
/// background, grid spacing and view transform to the newly active controller.
/// </summary>
public sealed class BoardPageState
{
    public string Name { get; set; } = "页面";

    public BoardBackground Background { get; set; } = BoardBackground.Black;

    /// <summary>Grid/lined/coordinate spacing in device-independent pixels.</summary>
    public float GridSpacing { get; set; } = 40f;

    /// <summary>Optional per-page view transform. Identity means the page is not panned or zoomed.</summary>
    public Matrix3x2 ViewTransform { get; set; } = Matrix3x2.Identity;

    public BoardPageState Clone() => new()
    {
        Name = Name,
        Background = Background,
        GridSpacing = GridSpacing,
        ViewTransform = ViewTransform,
    };
}