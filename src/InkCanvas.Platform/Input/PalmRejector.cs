using InkCanvas.Core.Input;
using InkCanvas.Core.Settings;
using InkCanvas.Platform.Windows;

namespace InkCanvas.Platform.Input;

/// <summary>
/// Decides whether a touch contact is a palm/back-of-hand rather than a fingertip,
/// using the contact rectangle the digitizer reports.
/// <para>
/// Deliberately has <b>no visual side effect</b>. The WPF original dimmed the whole ink
/// layer to <c>Opacity = 0.1</c> while a palm was detected and restored it afterwards,
/// which produced a visible flash across all existing ink. Rejection here only affects
/// how input is routed.
/// </para>
/// </summary>
public sealed class PalmRejector
{
    /// <summary>Contact diameter (px) above which a touch is treated as a palm.</summary>
    public float PalmContactThreshold { get; set; } = 30f;

    /// <summary>A pen in range suppresses touch drawing entirely (hardware palm rejection).</summary>
    public bool PenHasPriority { get; set; } = true;

    private readonly Dictionary<uint, long> _activePens = new();
    private readonly HashSet<uint> _palms = new();

    public bool IsPenActive => _activePens.Count > 0;
    public bool SuppressesTouch(in OverlayPointer p)
        => p.Kind == OverlayPointerKind.Touch && PenHasPriority && _activePens.Values.Contains(p.InputSurface);

    public IReadOnlyCollection<uint> ActivePalms => _palms;

    public void OnDown(in OverlayPointer p)
    {
        if (p.Kind == OverlayPointerKind.Pen) _activePens[p.Id] = p.InputSurface;
        else if (p.Kind == OverlayPointerKind.Touch && IsPalmContact(p)) _palms.Add(p.Id);
    }

    public void OnUp(in OverlayPointer p)
    {
        _activePens.Remove(p.Id);
        _palms.Remove(p.Id);
    }

    public void OnMove(in OverlayPointer p)
    {
        if (p.Kind == OverlayPointerKind.Touch && IsPalmContact(p)) _palms.Add(p.Id);
    }

    public bool IsPalmContact(in OverlayPointer p)
        => MathF.Max(p.ContactWidth, p.ContactHeight) >= PalmContactThreshold;

    /// <summary>
    /// Advanced settings driving the 1:1 special-screen classification. When null, the simple
    /// <see cref="PalmContactThreshold"/> behaviour above is used instead.
    /// </summary>
    public AdvancedSettings Advanced { get; set; }

    // Read-only fallback so ClassifyContact does not allocate a settings object per contact.
    private static readonly AdvancedSettings DefaultAdvanced = new();

    /// <summary>Original <c>Settings.Startup.IsEnableNibMode</c>.</summary>
    public bool NibMode { get; set; }

    public bool ForceEraser { get; set; }

    public bool ForcePointEraser { get; set; } = true;

    /// <summary>Slideshow running, no ink yet, and finger slide control enabled.</summary>
    public bool PptGestureEligible { get; set; }

    /// <summary>
    /// 1:1 touch classification including QuadIR contact measurement and the special-screen
    /// multiplier. <b>未验证</b>：按要求跳过行为验证，需在真实红外屏上复核。
    /// </summary>
    public TouchDecision ClassifyContact(in OverlayPointer p)
    {
        var advanced = Advanced ?? DefaultAdvanced;
        double width = TouchContactClassifier.MeasureContactWidth(
            p.ContactWidth, p.ContactHeight, advanced.IsQuadIR);
        return TouchContactClassifier.Classify(width, advanced, NibMode,
            ForceEraser, isDrawingShape: false, ForcePointEraser, PptGestureEligible);
    }

    /// <summary>True when this contact should be allowed to draw.</summary>
    public bool ShouldDraw(in OverlayPointer p)
    {
        if (p.Kind == OverlayPointerKind.Pen) return true;
        if (_palms.Contains(p.Id)) return false;
        if (SuppressesTouch(p)) return false;
        return true;
    }

    /// <summary>True when this contact should act as an area eraser (palm wipe).</summary>
    public bool ShouldErase(in OverlayPointer p) => p.Kind == OverlayPointerKind.Touch && _palms.Contains(p.Id);

    public void Reset()
    {
        _activePens.Clear();
        _palms.Clear();
    }
}
