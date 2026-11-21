using System.Numerics;

namespace InkCanvas.Platform.System;

/// <summary>Calibrates Word's scrollbar coordinates against visible character positions.</summary>
public sealed class WordScrollAlignment
{
    private Vector2? _previousAbsolute;
    private bool _reliable = true;

    public void Reset()
    {
        _previousAbsolute = null;
        _reliable = true;
    }

    public bool HasScrollJump(Vector2? absoluteScroll, float threshold) => absoluteScroll.HasValue
        && _previousAbsolute.HasValue && Vector2.Distance(absoluteScroll.Value, _previousAbsolute.Value) > threshold;

    public (Vector2 Scroll, bool HasMetrics, bool Reliable) Update(Vector2 previousScroll,
        float scaleRatio, Vector2? anchorMovement, Vector2? absoluteScroll, bool hasEstimate, bool referenceAligned = false)
    {
        Vector2 scroll = previousScroll * scaleRatio;
        bool known = false;
        if (anchorMovement.HasValue)
        {
            // Includes the horizontal displacement when Word re-centres the paper at a new zoom.
            scroll -= anchorMovement.Value;
            known = true;
            if (referenceAligned) _reliable = true;
        }
        else if (!hasEstimate && absoluteScroll.HasValue)
        {
            scroll = absoluteScroll.Value;
            known = true;
        }
        else if (Math.Abs(scaleRatio - 1) < .002f && absoluteScroll.HasValue && _previousAbsolute.HasValue)
        {
            // Use differences so returning to scrollbar sampling cannot discard anchor calibration.
            scroll = previousScroll + absoluteScroll.Value - _previousAbsolute.Value;
            known = true;
        }
        else if (hasEstimate)
            _reliable = false;

        _previousAbsolute = absoluteScroll;
        return (scroll, known, _reliable);
    }
}
