using System.Numerics;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class SideBoardLayoutTests
{
    private static readonly SideBoardWorkArea Work = new(0, 0, 1920, 1080);

    [Fact]
    public void DefaultIsRightDrawerAtFortyBySeventyPercent()
    {
        var rect = SideBoardLayout.Default(Work, 1);

        Assert.Equal(new SideBoardRect(1152, 162, 768, 756), rect);
        Assert.True(SideBoardLayout.FitsInside(rect, Work));
    }

    [Fact]
    public void MinimumIsHonouredButNeverExceedsWorkArea()
    {
        var rect = SideBoardLayout.Default(new SideBoardWorkArea(0, 0, 320, 240), 1);

        Assert.Equal(new SideBoardRect(0, 0, 320, 240), rect);
    }

    [Fact]
    public void DpiScalesTheMinimumAndKeepsFractions()
    {
        var rect = SideBoardLayout.Default(new SideBoardWorkArea(0, 0, 3840, 2160), 2);

        Assert.Equal(1536, rect.Width);
        Assert.Equal(1512, rect.Height);
        Assert.Equal(2304, rect.X);
        Assert.Equal(324, rect.Y);
    }

    [Fact]
    public void ValidSavedRectIsRestoredExactly()
    {
        var saved = new SideBoardSettings { X = 100, Y = 200, W = 600, H = 400 };

        Assert.True(saved.HasSavedBounds);
        Assert.Equal(new SideBoardRect(100, 200, 600, 400), SideBoardLayout.Resolve(saved, Work, 1));
    }

    [Fact]
    public void OutOfWorkAreaSavedRectFallsBackToDefault()
    {
        var saved = new SideBoardSettings { X = 1800, Y = 0, W = 600, H = 400 };

        Assert.Equal(SideBoardLayout.Default(Work, 1), SideBoardLayout.Resolve(saved, Work, 1));
    }

    [Fact]
    public void PartiallyOffscreenSavedRectFallsBackToDefault()
    {
        var saved = new SideBoardSettings { X = -20, Y = 200, W = 600, H = 400 };

        Assert.Equal(SideBoardLayout.Default(Work, 1), SideBoardLayout.Resolve(saved, Work, 1));
    }

    [Fact]
    public void IncompleteSavedRectFallsBackToDefault()
    {
        var saved = new SideBoardSettings { X = 100, Y = 200, W = 600 };

        Assert.False(saved.HasSavedBounds);
        Assert.Equal(SideBoardLayout.Default(Work, 1), SideBoardLayout.Resolve(saved, Work, 1));
    }

    [Fact]
    public void ClampKeepsMinimumSizeAndRectInsideWorkArea()
    {
        var clamped = SideBoardLayout.Clamp(new SideBoardRect(5000, -50, 100, 100), Work, 1);

        Assert.Equal(360, clamped.Width);
        Assert.Equal(280, clamped.Height);
        Assert.True(SideBoardLayout.FitsInside(clamped, Work));
    }
}

public sealed class SideBoardHotkeyTests
{
    [Fact]
    public void DefaultsToAltN()
    {
        var binding = new AppSettings().Hotkey.SideBoard;

        Assert.Equal("Alt", binding.Modifiers);
        Assert.Equal("N", binding.Key);
        Assert.Equal(0x4Eu, SideBoardHotkey.DefaultVirtualKey);
        Assert.Equal(0x4Eu, SideBoardHotkey.ParseVirtualKey(binding.Key));
    }

    [Fact]
    public void OldSettingsWithoutNodeFallBackToAltN()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"canvas\":{}}");

        Assert.NotNull(settings);
        Assert.Equal("Alt", settings!.Hotkey.SideBoard.Modifiers);
        Assert.Equal("N", settings.Hotkey.SideBoard.Key);
    }

    [Fact]
    public void NullNodesFallBackToDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"hotkey\":null,\"sideBoard\":null}");

        Assert.NotNull(settings);
        Assert.Equal(("Alt", "N"), SideBoardHotkey.Normalize(settings!.Hotkey));
        Assert.False(settings.SideBoard.HasSavedBounds);
    }

    [Fact]
    public void CustomModifiersAndFunctionKeyAreNormalized()
    {
        var settings = new AppSettings { Hotkey = new HotkeySettings { SideBoard = new HotkeyBindingSettings { Modifiers = "ctrl + alt", Key = "f5" } } };

        Assert.Equal(("Alt+Control", "F5"), SideBoardHotkey.Normalize(settings.Hotkey));
        Assert.Equal(0x74u, SideBoardHotkey.ParseVirtualKey("F5"));
    }

    [Fact]
    public void InvalidModifiersAndKeysFallBackToDefaults()
    {
        Assert.Equal(("Alt", "N"), SideBoardHotkey.Normalize(
            new HotkeySettings { SideBoard = new HotkeyBindingSettings { Modifiers = "hyper", Key = "ab" } }));
    }

    [Fact]
    public void ModifierParsingCombinesFlags()
    {
        Assert.True(SideBoardHotkey.TryParseModifiers("Ctrl+Shift", out var modifiers));
        Assert.Equal(SideBoardHotkeyModifiers.Control | SideBoardHotkeyModifiers.Shift, modifiers);
        Assert.False(SideBoardHotkey.TryParseModifiers("Hyper", out _));
    }
}

public sealed class SideBoardStateIsolationTests
{
    [Fact]
    public void SeparateServicesKeepPagesHistoryPaperAndViewIndependent()
    {
        var main = new BoardService();
        var side = new BoardService();

        main.Background = BoardBackground.Grid;
        main.GridSpacing = 56;
        main.Current.Document.Add(new InkStroke(new StrokeAttributes { BaseWidth = 5 }));
        main.Current.Clear();
        side.Current.Document.Add(new InkStroke(new StrokeAttributes { BaseWidth = 5 }));
        side.Current.Clear();
        side.Current.ViewTransform = Matrix3x2.CreateScale(2f);

        Assert.NotSame(main.Current, side.Current);
        Assert.Equal(1, main.Current.History.UndoCount);
        Assert.Equal(1, side.Current.History.UndoCount);
        Assert.Equal(BoardBackground.Grid, main.Background);
        Assert.Equal(BoardBackground.Black, side.Background);
        Assert.Equal(56, main.GridSpacing);
        Assert.Equal(40f, side.GridSpacing);
        Assert.Equal(Matrix3x2.Identity, main.Current.ViewTransform);
        Assert.Equal(Matrix3x2.CreateScale(2f), side.Current.ViewTransform);

        main.Current.Undo();

        Assert.Single(main.Current.Document.Strokes);
        Assert.Empty(side.Current.Document.Strokes);
        Assert.Equal(0, main.Current.History.UndoCount);
        Assert.Equal(1, side.Current.History.UndoCount);

        Assert.True(main.InsertPageAfterCurrent());
        Assert.Equal(2, main.PageCount);
        Assert.Equal(1, side.PageCount);
    }
}