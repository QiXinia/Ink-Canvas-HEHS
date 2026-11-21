using InkCanvas.Core.Settings;
using InkCanvas.Features.Persistence;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class AutomationPolicyTests
{
    [Theory]
    [InlineData("EasiNote")]
    [InlineData("EasiCamera")]
    [InlineData("BoardService")]
    [InlineData("seewoPincoTeacher")]
    [InlineData("HiteTouchPro")]
    [InlineData("HiteCamera")]
    [InlineData("WxBoardMain")]
    [InlineData("MicrosoftWhiteboard")]
    public void FoldRulesRecognizeExactVendorNames(string name)
    {
        var settings = new AutomationSettings { IsAutoFoldInEasiNote = true, IsAutoFoldInEasiCamera = true,
            IsAutoFoldInSeewoPincoTeacher = true, IsAutoFoldInHiteTouchPro = true, IsAutoFoldInHiteCamera = true,
            IsAutoFoldInWxBoardMain = true, IsAutoFoldInMSWhiteboard = true };
        Assert.True(AutomationPolicy.ShouldFold(settings, name.ToLowerInvariant(), "Classroom", 800, false));
        Assert.False(AutomationPolicy.ShouldFold(settings, "other" + name, "Classroom", 800, false));
    }

    [Fact]
    public void DesktopAnnotationExceptionAndLightWhiteboardFollowIndependentWpfOptions()
    {
        var settings = new AutomationSettings { IsAutoFoldInEasiNote = true, IsAutoFoldInEasiNoteIgnoreDesktopAnno = true };
        Assert.False(AutomationPolicy.ShouldFold(settings, "EasiNote", "", 200, false));
        Assert.True(AutomationPolicy.ShouldFold(settings, "EasiNote", "Document", 200, false));
        Assert.True(AutomationPolicy.ShouldFold(settings, "EasiNote", "", 800, false));
        settings.IsAutoFoldInEasiNoteIgnoreDesktopAnno = false;
        Assert.True(AutomationPolicy.ShouldFold(settings, "EasiNote", "", 200, false));
        settings.IsAutoFoldInEasiNote = false;
        Assert.False(AutomationPolicy.ShouldFold(settings, "EasiNote", "", 200, false));
        settings.IsAutoFoldInEasiNote3C = true; settings.IsAutoFoldInEasiNoteIgnoreDesktopAnno = true;
        Assert.True(AutomationPolicy.ShouldFold(settings, "EasiNote", "", 200, false));
    }

    [Fact]
    public void WhiteboardDoesNotMatchEveryWebViewAndOldBoardUsesExactTitle()
    {
        var settings = new AutomationSettings { IsAutoFoldInMSWhiteboard = true, IsAutoFoldInOldZyBoard = true };
        Assert.False(AutomationPolicy.ShouldFold(settings, "msedgewebview2", "Unrelated app", 800, false));
        Assert.True(AutomationPolicy.ShouldFold(settings, "legacy", "WhiteBoard - DrawingWindow", 800, false));
        Assert.False(AutomationPolicy.ShouldFold(settings, "legacy", "Other WhiteBoard", 800, false));
    }

    [Fact]
    public void SlideShowFoldAndClearRulesHonorBothOptions()
    {
        var settings = new AutomationSettings { IsAutoFoldInPPTSlideShow = true, IsAutoClearWhenExitingWritingMode = true };
        var ppt = new PowerPointSettings { IsNoClearStrokeOnSelectWhenInPowerPoint = true };
        Assert.True(AutomationPolicy.ShouldFold(settings, "", "", 0, true));
        Assert.False(AutomationPolicy.ShouldFold(settings, "POWERPNT", "", 0, false));
        Assert.True(AutomationPolicy.ShouldClearOnMouseMode(settings, ppt, false));
        Assert.False(AutomationPolicy.ShouldClearOnMouseMode(settings, ppt, true));
        ppt.IsNoClearStrokeOnSelectWhenInPowerPoint = false;
        Assert.True(AutomationPolicy.ShouldClearOnMouseMode(settings, ppt, true));
        settings.IsAutoClearWhenExitingWritingMode = false;
        Assert.False(AutomationPolicy.ShouldClearOnMouseMode(settings, ppt, false));
    }

    [Fact]
    public void ContactCalibrationUsesReportedSizeAndRejectsMissingSize()
    {
        Assert.Equal(5 / 11d, AutomationPolicy.CalibrateTouch(10, 40, false), 6);
        Assert.Equal(5 / 22d, AutomationPolicy.CalibrateTouch(10, 40, true), 6);
        Assert.True(double.IsNaN(AutomationPolicy.CalibrateTouch(0, 0, false)));
        Assert.InRange(AutomationPolicy.CalibrateTouch(.01, .01, true), .01, 5);
    }

    [Fact]
    public void CleanupIsOptInAndPreservesUnrelatedAndRecentFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "InkCanvas-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string oldInk = FileAt("old.icink", 31);
            string newInk = FileAt("recent.icink", 1);
            string oldPicture = FileAt("personal.png", 31);
            string oldScreenshot = FileAt(Path.Combine("Screenshots", "Screenshot-121212.png"), 31);
            string personalInFolder = FileAt(Path.Combine("Screenshots", "personal.png"), 31);
            string pptScreenshot = FileAt(Path.Combine("Auto Saved - PPT Screenshots", "presentation", "1.png"), 31);
            var settings = new AutomationSettings { AutoSavedStrokesLocation = directory, AutoDelSavedFilesDaysThreshold = 15 };
            var service = new AutoSaveService(settings);
            Assert.Equal(0, service.Prune());
            Assert.True(File.Exists(oldInk));
            settings.AutoDelSavedFiles = true;
            Assert.Equal(3, service.Prune());
            Assert.False(File.Exists(oldInk));
            Assert.False(File.Exists(oldScreenshot));
            Assert.False(File.Exists(pptScreenshot));
            Assert.True(File.Exists(newInk));
            Assert.True(File.Exists(oldPicture));
            Assert.True(File.Exists(personalInFolder));
            Assert.Equal(0, service.Prune());
        }
        finally { Directory.Delete(directory, recursive: true); }

        string FileAt(string name, int days)
        {
            string path = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "fixture");
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-days));
            return path;
        }
    }
}
