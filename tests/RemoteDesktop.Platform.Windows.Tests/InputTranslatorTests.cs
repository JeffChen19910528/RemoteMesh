using RemoteDesktop.Input;
using RemoteDesktop.Platform.Windows;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>The mapping from the wire format to Windows input. Pure logic: runs on any OS.</summary>
public sealed class InputTranslatorTests
{
    private static ushort Scan(ushort vk) => (ushort)(vk + 1000); // recognizable fake scan code

    private static InputEvent Ev(InputEventType t, int x = 0, int y = 0, int button = 0, int delta = 0, int key = 0) =>
        new() { EventType = t, X = x, Y = y, Button = button, Delta = delta, KeyCode = key };

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(32767, 32767, 65535, 65535)]
    [InlineData(16383, 0, 32766, 0)]
    public void Mouse_Coordinates_ScaleToTheAbsoluteRange(int x, int y, int expectedX, int expectedY)
    {
        var spec = Assert.Single(InputTranslator.Translate(Ev(InputEventType.MouseMove, x, y), Scan));
        Assert.False(spec.IsKey);
        Assert.Equal(expectedX, spec.X);
        Assert.Equal(expectedY, spec.Y);
        Assert.Equal(InputTranslator.MOVE | InputTranslator.ABSOLUTE, spec.Flags);
    }

    [Fact]
    public void Mouse_OutOfRangeCoordinates_AreClamped_NotWrapped()
    {
        var spec = Assert.Single(InputTranslator.Translate(Ev(InputEventType.MouseMove, int.MaxValue, -5), Scan));
        Assert.Equal(65535, spec.X);
        Assert.Equal(0, spec.Y);
    }

    [Theory]
    [InlineData(1, true, InputTranslator.LEFTDOWN)]
    [InlineData(1, false, InputTranslator.LEFTUP)]
    [InlineData(2, true, InputTranslator.RIGHTDOWN)]
    [InlineData(2, false, InputTranslator.RIGHTUP)]
    [InlineData(3, true, InputTranslator.MIDDLEDOWN)]
    [InlineData(3, false, InputTranslator.MIDDLEUP)]
    public void Buttons_MapToTheRightFlags_AndCarryThePosition(int button, bool down, uint flag)
    {
        var type = down ? InputEventType.MouseButtonDown : InputEventType.MouseButtonUp;
        var spec = Assert.Single(InputTranslator.Translate(Ev(type, 100, 200, button), Scan));
        Assert.Equal(InputTranslator.MOVE | InputTranslator.ABSOLUTE | flag, spec.Flags); // click lands where pointed
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(99)]
    public void UnknownButtons_AreIgnored(int button)
    {
        Assert.Empty(InputTranslator.Translate(Ev(InputEventType.MouseButtonDown, 1, 1, button), Scan));
    }

    [Fact]
    public void Wheel_KeepsTheSignedDelta()
    {
        var up = Assert.Single(InputTranslator.Translate(Ev(InputEventType.MouseWheel, delta: 120), Scan));
        var down = Assert.Single(InputTranslator.Translate(Ev(InputEventType.MouseWheel, delta: -240), Scan));
        Assert.Equal(120, up.MouseData);
        Assert.Equal(-240, down.MouseData);
        Assert.Equal(InputTranslator.WHEEL, up.Flags);
    }

    [Fact]
    public void Keys_UseTheVirtualKeyAndItsScanCode()
    {
        var down = Assert.Single(InputTranslator.Translate(Ev(InputEventType.KeyDown, key: 0x41), Scan));
        var up = Assert.Single(InputTranslator.Translate(Ev(InputEventType.KeyUp, key: 0x41), Scan));
        Assert.True(down.IsKey);
        Assert.Equal(0x41, down.VirtualKey);
        Assert.Equal(Scan(0x41), down.ScanCode);
        Assert.Equal(0u, down.Flags);
        Assert.Equal(InputTranslator.KEYEVENTF_KEYUP, up.Flags);
    }

    [Theory]
    [InlineData(0x25)] // Left arrow
    [InlineData(0x2E)] // Delete
    [InlineData(0x5B)] // Left Windows
    [InlineData(0xA3)] // Right Ctrl
    public void ExtendedKeys_AreFlagged(int vk)
    {
        var spec = Assert.Single(InputTranslator.Translate(Ev(InputEventType.KeyDown, key: vk), Scan));
        Assert.Equal(InputTranslator.KEYEVENTF_EXTENDEDKEY, spec.Flags);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0x100)]
    [InlineData(0xFFFF)]
    public void KeyCodes_OutsideTheVirtualKeyRange_AreDropped(int vk)
    {
        Assert.Empty(InputTranslator.Translate(Ev(InputEventType.KeyDown, key: vk), Scan));
    }
}
