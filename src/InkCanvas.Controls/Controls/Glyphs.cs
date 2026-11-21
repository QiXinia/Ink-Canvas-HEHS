namespace InkCanvas.Controls.Controls;

/// <summary>
/// Icon codepoints, named so call sites never contain raw private-use characters.
/// Everything here is Segoe Fluent Icons / Segoe MDL2 Assets except <see cref="Shapes"/>,
/// which is a geometric character from Segoe UI Symbol (set <c>IconFont</c> when using it).
/// </summary>
public static class Glyphs
{
    public const string Pen = "";
    public const string Highlight = "";
    public const string Eraser = "";
    public const string Select = "";

    public const string Shapes = "△";
    public const string ShapesFont = "Segoe UI Symbol";

    /// <summary>Sentinel instead of an icon codepoint: hosts render the literal text "AI".</summary>
    public const string Ai = "AI";

    public const string Undo = "";
    public const string Redo = "";
    public const string Delete = "";
    public const string Add = "";

    public const string Settings = "";
    public const string Camera = "";
    public const string Video = "\uE714";
    public const string Stopwatch = "";
    public const string People = "";
    public const string Board = "";

    public const string ChevronUp = "";
    public const string ChevronDown = "";
    public const string ChevronLeft = "";
    public const string ChevronRight = "";

    public const string Lock = "";
    public const string Unlock = "";
    public const string Close = "";
    public const string Exit = "";

    public const string Play = "";
    public const string Pause = "";
    public const string Restart = "";
    public const string Replay = "";
    public const string Help = "";
    public const string Info = "";
    public const string Download = "";
    public const string TimeMachine = "\uE81C";
    public const string SlideList = "\uE8FD";

    public const string Pdf = "\uEA90";
    public const string Import = "\uE8B5";
    public const string Calculator = "\uE8EF";
    // ---- Exact WPF floating-bar codepoints (upstream MainWindow.xaml) ----
    public const string Cursor = "";
    public const string PenWpf = "";
    public const string HighlightWpf = "\uE7E6"; // Highlight, verified in the bundled Segoe MDL2 Assets font.
    public const string EraserOverlayCircle = "";
    public const string EraserOverlayStroke = "";
    public const string SelectWpf = "";
    public const string SelectOverlay = "";
    public const string ShapesWpf = "";
    public const string InsertMedia = "";
    public const string BoardWpf = "";
    public const string Tools = "";
    public const string FoldWpf = "";
    public const string Emoji = "";
    public const string EmojiDrag = "";
    public const string Gesture = "";
    public const string TimerWpf = "";
    public const string Rand = "";
    public const string SaveStrokes = "";
    public const string OpenStrokes = "";
    public const string ReplayWpf = "";
    public const string GuideWpf = "";
    public const string ExitWpf = "";
    public const string HideControl = "";
    public const string LRSwitch = "";
    public const string GestureTranslate = "";
    public const string GestureZoom = "";
    public const string GestureRotate = "";
}
