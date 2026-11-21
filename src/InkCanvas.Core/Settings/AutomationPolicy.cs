namespace InkCanvas.Core.Settings;

public static class AutomationPolicy
{
    public static bool ShouldFold(AutomationSettings settings, string process, string title, int height, bool slideShow)
    {
        if (slideShow && settings.IsAutoFoldInPPTSlideShow) return true;
        bool Is(string name) => string.Equals(process, name, StringComparison.OrdinalIgnoreCase);
        if (Is("EasiNote"))
            return settings.IsAutoFoldInEasiNote3C || settings.IsAutoFoldInEasiNote
                && !(settings.IsAutoFoldInEasiNoteIgnoreDesktopAnno && string.IsNullOrEmpty(title) && height is >= 0 and < 500);
        if (Is("EasiCamera")) return settings.IsAutoFoldInEasiCamera;
        if (Is("BoardService") || Is("seewoPincoTeacher")) return settings.IsAutoFoldInSeewoPincoTeacher;
        if (Is("HiteTouchPro")) return settings.IsAutoFoldInHiteTouchPro;
        if (Is("HiteCamera")) return settings.IsAutoFoldInHiteCamera;
        if (Is("WxBoardMain")) return settings.IsAutoFoldInWxBoardMain;
        if (Is("MicrosoftWhiteboard")) return settings.IsAutoFoldInMSWhiteboard;
        return settings.IsAutoFoldInOldZyBoard && title is "WhiteBoard - DrawingWindow" or "InstantAnnotationWindow";
    }

    public static bool ShouldClearOnMouseMode(AutomationSettings automation, PowerPointSettings presentation, bool slideShow)
        => automation.IsAutoClearWhenExitingWritingMode && (!slideShow || !presentation.IsNoClearStrokeOnSelectWhenInPowerPoint);

    public static double CalibrateTouch(double width, double height, bool quadIr)
    {
        double contact = quadIr ? Math.Sqrt(width * height) : width;
        return double.IsFinite(contact) && contact > 0 ? Math.Clamp(5 / (contact * 1.1), .01, 5) : double.NaN;
    }
}
