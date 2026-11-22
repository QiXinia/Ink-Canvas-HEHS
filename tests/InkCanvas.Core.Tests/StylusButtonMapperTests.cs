using InkCanvas.Ink;
using InkCanvas.Modules.Stylus;
using InkCanvas.Platform.Windows;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class StylusButtonMapperTests
{
    private static StylusBinding Binding(string id, StylusAction action = StylusAction.AreaEraser,
        StylusSwitchMode mode = StylusSwitchMode.Temporary) => new(id, id, action, mode);

    [Fact]
    public void TemporaryButtonsRestoreInOrderAndIgnoreRepeatedPresses()
    {
        using var controller = new InkController { EraserMode = InkEraserMode.Lasso };
        using var mapper = new StylusButtonMapper();
        var barrel = Binding("barrel");
        var secondary = Binding("secondary", StylusAction.Select);
        mapper.SetButton(controller, barrel, true);
        mapper.SetButton(controller, barrel, true);
        Assert.Equal(InkTool.Eraser, controller.Tool);
        mapper.SetButton(controller, secondary, true);
        Assert.Equal(InkTool.Select, controller.Tool);
        mapper.SetButton(controller, secondary, false);
        Assert.Equal(InkTool.Eraser, controller.Tool);
        mapper.SetButton(controller, barrel, false);
        Assert.Equal(InkTool.Pen, controller.Tool);
        Assert.Equal(InkEraserMode.Lasso, controller.EraserMode);
        Assert.False(mapper.HasPressedButtons);
    }

    [Fact]
    public void TogglePersistsAfterReleaseAndSecondPressRestoresHighlighter()
    {
        using var controller = new InkController { Tool = InkTool.Highlighter };
        using var mapper = new StylusButtonMapper();
        var toggle = Binding("key:113:0", mode: StylusSwitchMode.Toggle);
        mapper.SetButton(controller, toggle, true);
        mapper.SetButton(controller, toggle, false);
        Assert.Equal(InkTool.Eraser, controller.Tool);
        mapper.SetButton(controller, toggle, true);
        mapper.SetButton(controller, toggle, false);
        Assert.Equal(InkTool.Highlighter, controller.Tool);
    }

    [Fact]
    public void PermanentMappingKeepsItsToolAfterReleaseAndReset()
    {
        using var controller = new InkController();
        using var mapper = new StylusButtonMapper();
        var binding = Binding("pad", StylusAction.Select, StylusSwitchMode.Permanent);
        mapper.SetButton(controller, binding, true);
        mapper.SetButton(controller, binding, false);
        mapper.Reset();
        Assert.Equal(InkTool.Select, controller.Tool);
    }

    [Fact]
    public void ManualToolChoiceSupersedesHeldButtonRestoration()
    {
        using var controller = new InkController();
        using var mapper = new StylusButtonMapper();
        var binding = Binding("barrel");
        mapper.SetButton(controller, binding, true);
        controller.Tool = InkTool.Highlighter;
        mapper.SetButton(controller, binding, false);
        Assert.Equal(InkTool.Highlighter, controller.Tool);
    }

    [Fact]
    public void MidStrokeChangesWaitForContactEndAndPreserveUndo()
    {
        using var controller = new InkController();
        using var mapper = new StylusButtonMapper();
        var pointer = new OverlayPointer(31, 10, 10, .5f, 0, 0, OverlayPointerKind.Pen, 1);
        controller.OnPointerDown(pointer);
        mapper.SetButton(controller, Binding("barrel"), true);
        Assert.Equal(InkTool.Pen, controller.Tool);
        controller.OnPointerUp(pointer with { X = 50, TimeMs = 20 });
        mapper.Flush();
        Assert.Equal(InkTool.Eraser, controller.Tool);
        Assert.Single(controller.Document.Strokes);
        mapper.SetButton(controller, Binding("barrel"), false);
        Assert.Equal(InkTool.Pen, controller.Tool);
        controller.Undo();
        Assert.Empty(controller.Document.Strokes);
    }

    [Fact]
    public void ReleaseDuringEraseFinishesBeforeRestoringPen()
    {
        using var controller = new InkController();
        using var mapper = new StylusButtonMapper();
        var binding = Binding("barrel");
        var pointer = new OverlayPointer(31, 10, 10, .5f, 0, 0, OverlayPointerKind.Pen, 1);
        mapper.SetButton(controller, binding, true);
        controller.OnPointerDown(pointer);
        mapper.SetButton(controller, binding, false);
        Assert.Equal(InkTool.Eraser, controller.Tool);
        controller.OnPointerUp(pointer);
        mapper.Flush();
        Assert.Equal(InkTool.Pen, controller.Tool);
        Assert.False(controller.HasActiveInteraction);
    }

    [Fact]
    public void ChangingTargetAndDisablingRestoresOnlyOwnedTools()
    {
        using var first = new InkController();
        using var second = new InkController { Tool = InkTool.Highlighter };
        using var mapper = new StylusButtonMapper();
        var binding = Binding("barrel");
        mapper.SetButton(first, binding, true);
        mapper.SetButton(second, binding, true);
        Assert.Equal(InkTool.Pen, first.Tool);
        Assert.Equal(InkTool.Eraser, second.Tool);
        mapper.Reset();
        Assert.Equal(InkTool.Highlighter, second.Tool);
    }

    [Fact]
    public void SynthesizedDriverKeysAreConsumedOnceAndTheirReleaseIsBalanced()
    {
        var signals = new List<StylusButtonSignal>();
        var type = typeof(StylusSettings).Assembly.GetType("InkCanvas.Modules.Stylus.ButtonInputMonitor");
        Func<StylusButtonSignal, bool> handle = signal => { signals.Add(signal); return true; };
        using var monitor = (IDisposable)Activator.CreateInstance(type, handle);
        var keyboard = type.GetMethod("Keyboard", BindingFlags.NonPublic | BindingFlags.Instance);
        nint data = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(new byte[24], 0, data, 24);
            Marshal.WriteInt32(data, 0, 124);
            Marshal.WriteInt32(data, 8, 0x10);
            Assert.Equal((nint)1, keyboard.Invoke(monitor, new object[] { 0, (nint)0x100, data }));
            Assert.Equal((nint)1, keyboard.Invoke(monitor, new object[] { 0, (nint)0x100, data }));
            Assert.Equal((nint)1, keyboard.Invoke(monitor, new object[] { 0, (nint)0x101, data }));
            Assert.Equal(2, signals.Count);
            Assert.True(signals[0].Pressed);
            Assert.False(signals[1].Pressed);
            Assert.Equal(signals[0].Input, signals[1].Input);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    [Fact]
    public void SettingsPersistLearnedButtonsAndRecoverFromDamagedFiles()
    {
        string directory = Directory.CreateTempSubdirectory("stylus-settings-test-").FullName;
        try
        {
            var settings = new StylusSettings();
            settings.Bindings.Add(Binding("key:114:3", StylusAction.Undo, StylusSwitchMode.Permanent));
            settings.Save(directory);
            Assert.Equal(settings.Bindings, StylusSettings.Load(directory).Bindings);
            File.WriteAllText(Path.Combine(directory, "buttons.json"), "{broken");
            Assert.Equal(3, StylusSettings.Load(directory).Bindings.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
