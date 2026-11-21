using InkCanvas.Platform.Input;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class HotkeyScopeTests
{
    private sealed class Keyboard
    {
        public readonly Dictionary<int, (uint Modifiers, uint Key)> Registered = new();
        public readonly List<int> Released = new();
        public int Attempts;
        public bool Available = true;
        public bool Register(nint window, int id, uint modifiers, uint key)
        {
            Attempts++;
            if (!Available) return false;
            Registered.Add(id, (modifiers, key)); return true;
        }
        public bool Unregister(nint window, int id)
        {
            Released.Add(id); return Registered.Remove(id);
        }
        public HotkeyService Service() => new(0, Register, Unregister);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0x5A)]
    [InlineData(HotkeyModifiers.Control, 0x59)]
    [InlineData(HotkeyModifiers.Control, 0x45)]
    [InlineData(HotkeyModifiers.Alt, 0x45)]
    [InlineData(HotkeyModifiers.Alt, 0x53)]
    [InlineData(HotkeyModifiers.Alt, 0x4C)]
    public void MouseModeReleasesTheActualRegistrationAndRejectsQueuedMessages(HotkeyModifiers modifiers, uint key)
    {
        var keyboard = new Keyboard(); using var service = keyboard.Service();
        bool writing = false; int calls = 0;
        Assert.True(service.Register(modifiers, key, () => calls++, () => writing));
        Assert.Empty(keyboard.Registered);
        writing = true; service.Refresh();
        int first = Assert.Single(keyboard.Registered).Key;
        Assert.Equal((uint)(modifiers | HotkeyModifiers.NoRepeat), keyboard.Registered[first].Modifiers);
        Assert.True(service.Dispatch(first)); Assert.Equal(1, calls);
        writing = false;
        Assert.False(service.Dispatch(first)); Assert.Equal(1, calls);
        Assert.Empty(keyboard.Registered); Assert.Contains(first, keyboard.Released);
        writing = true; service.Refresh();
        int second = Assert.Single(keyboard.Registered).Key;
        Assert.NotEqual(first, second);
        Assert.False(service.Dispatch(first)); Assert.Equal(1, calls);
        Assert.True(service.Dispatch(second)); Assert.Equal(2, calls);
    }

    [Fact]
    public void GlobalModeSwitchShortcutsRemainAvailableInMouseMode()
    {
        var keyboard = new Keyboard(); using var service = keyboard.Service();
        bool editing = false; int activated = 0;
        service.Register(HotkeyModifiers.Control, 0x5A, () => throw new Exception("unexpected edit"), () => editing);
        service.Register(HotkeyModifiers.Alt, 0x44, () => activated++);
        service.Refresh();
        var global = Assert.Single(keyboard.Registered);
        Assert.Equal(0x44u, global.Value.Key);
        Assert.True(service.Dispatch(global.Key)); Assert.Equal(1, activated);
    }

    [Fact]
    public void FocusAndModeChangesReleaseEveryEditingBindingWithoutRegistrationChurn()
    {
        var keyboard = new Keyboard(); using var service = keyboard.Service();
        bool editing = true;
        service.Register(HotkeyModifiers.Control, 0x5A, () => { }, () => editing);
        service.Register(HotkeyModifiers.Control, 0x59, () => { }, () => editing);
        for (int i = 0; i < 50; i++) service.Refresh();
        Assert.Equal(2, keyboard.Attempts);
        editing = false; service.Refresh(); Assert.Empty(keyboard.Registered); Assert.Equal(2, keyboard.Released.Count);
        editing = true; service.Refresh(); Assert.Equal(2, keyboard.Registered.Count);
    }

    [Fact]
    public void ShortcutThatChangesModeImmediatelyReleasesOtherEditingKeys()
    {
        var keyboard = new Keyboard(); using var service = keyboard.Service();
        bool editing = true;
        service.Register(HotkeyModifiers.Control, 0x5A, () => editing = false, () => editing);
        service.Register(HotkeyModifiers.Control, 0x59, () => throw new Exception("unexpected redo"), () => editing);
        int undo = keyboard.Registered.Single(item => item.Value.Key == 0x5A).Key;
        Assert.True(service.Dispatch(undo)); Assert.Empty(keyboard.Registered);
    }

    [Fact]
    public void FailedRegistrationCanRecoverWhenTheKeyBecomesAvailable()
    {
        var keyboard = new Keyboard { Available = false }; using var service = keyboard.Service();
        int calls = 0;
        Assert.False(service.Register(HotkeyModifiers.Control, 0x5A, () => calls++, () => true));
        Assert.False(service.Dispatch(0x1000));
        keyboard.Available = true; service.Refresh();
        Assert.True(service.Dispatch(Assert.Single(keyboard.Registered).Key)); Assert.Equal(1, calls);
    }

    [Fact]
    public void DisposalAndUnregisterAllCannotResurrectBindings()
    {
        var keyboard = new Keyboard(); var service = keyboard.Service();
        service.Register(HotkeyModifiers.Control, 0x5A, () => throw new Exception("disposed action"));
        int id = Assert.Single(keyboard.Registered).Key;
        service.UnregisterAll(); service.Refresh(); Assert.Empty(keyboard.Registered); Assert.False(service.Dispatch(id));
        service.Register(HotkeyModifiers.Control, 0x59, () => { });
        service.Dispose(); service.Dispose(); service.Refresh(); Assert.Empty(keyboard.Registered);
        Assert.Throws<ObjectDisposedException>(() => service.Register(HotkeyModifiers.Control, 0x5A, () => { }));
    }
}
