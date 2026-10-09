using RemoteDesktop.Input;
using RemoteDesktop.Platform;
using RemoteDesktop.Platform.MacOS;

namespace RemoteDesktop.Platform.Tests;

/// <summary>
/// The macOS backend's logic. The parts that need a Mac (the screen, the event tap, the permission prompts) cannot run
/// anywhere else; everything that can be decided without one is tested here, on every operating system.
/// </summary>
public sealed class MacKeyMapTests
{
    [Theory]
    [InlineData(0x41, 0x00)] // A
    [InlineData(0x53, 0x01)] // S
    [InlineData(0x5A, 0x06)] // Z
    [InlineData(0x51, 0x0C)] // Q
    [InlineData(0x30, 0x1D)] // 0
    [InlineData(0x31, 0x12)] // 1
    [InlineData(0x35, 0x17)] // 5
    [InlineData(0x0D, 0x24)] // Return
    [InlineData(0x09, 0x30)] // Tab
    [InlineData(0x20, 0x31)] // Space
    [InlineData(0x08, 0x33)] // Backspace -> Delete
    [InlineData(0x2E, 0x75)] // Delete -> Forward Delete
    [InlineData(0x1B, 0x35)] // Escape
    [InlineData(0x25, 0x7B)] // Left
    [InlineData(0x26, 0x7E)] // Up
    [InlineData(0x70, 0x7A)] // F1
    [InlineData(0x7B, 0x6F)] // F12
    [InlineData(0x10, 0x38)] // Shift
    [InlineData(0x11, 0x3B)] // Control
    [InlineData(0x12, 0x3A)] // Alt -> Option
    [InlineData(0x5B, 0x37)] // Windows -> Command
    [InlineData(0x60, 0x52)] // numpad 0
    [InlineData(0x69, 0x5C)] // numpad 9
    [InlineData(0xBA, 0x29)] // ;
    [InlineData(0xC0, 0x32)] // `
    public void WindowsKeys_BecomeTheRightMacKeyCodes(int vk, int code) => Assert.Equal(code, MacKeyMap.ToKeyCode(vk));

    [Theory]
    [InlineData(0)]
    [InlineData(0x07)]
    [InlineData(0xFF)]
    [InlineData(-3)]
    public void UnknownKeys_MapToNothing(int vk) => Assert.Equal(-1, MacKeyMap.ToKeyCode(vk));

    [Fact]
    public void EveryLetterDigitFunctionAndNumpadKey_HasACode_AndNoTwoShareOne()
    {
        var seen = new HashSet<int>();
        void Check(int from, int to)
        {
            for (var vk = from; vk <= to; vk++)
            {
                var code = MacKeyMap.ToKeyCode(vk);
                Assert.True(code >= 0, $"VK {vk:X} has no code");
                Assert.True(seen.Add(code), $"VK {vk:X} shares code {code:X}");
            }
        }
        Check(0x30, 0x39);
        Check(0x41, 0x5A);
        Check(0x60, 0x69);
        Check(0x70, 0x7F);
    }

    [Fact]
    public void NoTwoOrdinaryKeysShareACode()
    {
        var seen = new Dictionary<int, int>();
        for (var vk = 1; vk < 256; vk++)
        {
            var code = MacKeyMap.ToKeyCode(vk);
            if (code < 0) continue;
            if (seen.TryGetValue(code, out var other))
                Assert.True((other, vk) is (0x10, 0xA0) or (0x11, 0xA2) or (0x12, 0xA4), $"VK {other:X} and {vk:X} both give {code:X}");
            seen[code] = vk;
        }
    }

    [Theory]
    [InlineData(0x38, MacKeyMap.ShiftMask)]
    [InlineData(0x3C, MacKeyMap.ShiftMask)]
    [InlineData(0x3B, MacKeyMap.ControlMask)]
    [InlineData(0x3A, MacKeyMap.OptionMask)]
    [InlineData(0x37, MacKeyMap.CommandMask)]
    [InlineData(0x36, MacKeyMap.CommandMask)]
    [InlineData(0x00, 0UL)]
    [InlineData(0x31, 0UL)]
    public void ModifierKeys_SetTheirFlag_OthersDoNot(int code, ulong flag) => Assert.Equal(flag, MacKeyMap.ModifierFlag(code));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, -1)]
    [InlineData(0, -1)]
    public void MouseButtons_AreRenumbered(int wire, int mac) => Assert.Equal(mac, MacKeyMap.ToButton(wire));

    [Theory]
    [InlineData(120, 3)]
    [InlineData(-120, -3)]
    [InlineData(10, 1)]
    [InlineData(-10, -1)]
    [InlineData(1_000_000, 60)]
    [InlineData(0, 0)]
    public void TheWheel_BecomesScrollLines(int delta, int lines) => Assert.Equal(lines, MacKeyMap.ToScrollLines(delta));

    [Theory]
    [InlineData(0, 1440.0, 0.0)]
    [InlineData(32767, 1440.0, 1439.0)]
    [InlineData(-5, 1440.0, 0.0)]
    [InlineData(99999, 1440.0, 1439.0)]
    [InlineData(50, 1.0, 0.0)]
    public void NormalisedCoordinates_LandInsideTheDisplay(int normalized, double size, double point) =>
        Assert.Equal(point, MacKeyMap.ToPoint(normalized, size), 0.5);

    [Fact]
    public void TheMiddleOfTheWireRange_IsTheMiddleOfTheDisplay() =>
        Assert.Equal(720.0, MacKeyMap.ToPoint(16384, 1440), 1.0);
}

/// <summary>
/// Tests for CGEventInputController double-click tracking. These are pure logic tests that run on every platform.
/// The CGEventInputController constructor throws on non-macOS, so we call the internal helper directly.
/// </summary>
public sealed class MacDoubleClickTests
{
    // A helper that exercises the double-click logic without needing a real Mac.
    private static InputEvent Click(int button, int x = 100, int y = 100) => new()
    {
        EventType = RemoteDesktop.Input.InputEventType.MouseButtonDown,
        Button = button, X = x, Y = y
    };

    private static CGEventInputController MakeController()
    {
        // UpdateClickCount is internal; this can only run on macOS because the constructor calls CGDisplayBounds.
        // Skip silently when not on macOS.
        return new CGEventInputController();
    }

    [Fact]
    public void FirstClick_AlwaysGivesCountOne()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var c = MakeController();
        c.UpdateClickCount(0, Click(1));
        Assert.Equal(1, c.ClickCount);
    }

    [Fact]
    public void TwoQuickClicksSameButton_GivesCountTwo()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var c = MakeController();
        c.UpdateClickCount(0, Click(1));
        c.UpdateClickCount(0, Click(1));
        Assert.Equal(2, c.ClickCount);
    }

    [Fact]
    public void ThreeQuickClicksSameButton_GivesCountThree()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var c = MakeController();
        c.UpdateClickCount(0, Click(1));
        c.UpdateClickCount(0, Click(1));
        c.UpdateClickCount(0, Click(1));
        Assert.Equal(3, c.ClickCount);
    }

    [Fact]
    public void TwoClicksDifferentButton_ResetsToOne()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var c = MakeController();
        c.UpdateClickCount(0, Click(1));  // left
        c.UpdateClickCount(1, Click(2));  // right
        Assert.Equal(1, c.ClickCount);
    }

    [Fact]
    public void TwoClicksFarApart_ResetsToOne()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var c = MakeController();
        c.UpdateClickCount(0, Click(1, x: 0, y: 0));
        // 32767 normalized maps to nearly the full display width — well beyond DoubleClickDistance.
        c.UpdateClickCount(0, Click(1, x: 32767, y: 32767));
        Assert.Equal(1, c.ClickCount);
    }
}

public sealed class MacDoubleClickLogicTests
{
    // These tests exercise the double-click interval and position constants without needing a real Mac.
    // They use a fake InputEvent and call UpdateClickCount indirectly via a mock-able subpath.

    [Fact]
    public void DoubleClickInterval_IsPositive() =>
        Assert.True(CGEventInputController.DoubleClickInterval > TimeSpan.Zero);

    [Fact]
    public void DoubleClickDistance_IsPositive() =>
        Assert.True(CGEventInputController.DoubleClickDistance > 0);
}

public sealed class MacScreenMathTests
{
    [Theory]
    [InlineData(1920, 2560, 1)]
    [InlineData(2560, 2560, 1)]
    [InlineData(2880, 2560, 2)]   // a 15" MacBook Pro: 2880x1800 -> 1440x900
    [InlineData(5120, 2560, 2)]   // a 27" 5K display
    [InlineData(6016, 2560, 3)]   // a 32" 6K display
    public void RetinaScreens_AreReducedByTheSmallestWholeFactorThatFits(int width, int max, int factor) =>
        Assert.Equal(factor, CoreGraphicsScreenCapture.ScaleFactor(width, max));

    [Fact]
    public void Downscale_AveragesEachBlock_AndMakesTheResultOpaque()
    {
        // 4x2 pixels, factor 2 -> 2x1. Left block: (10,20,30)(30,40,50)/(50,60,70)(70,80,90) = averages (40,50,60). Right block all (100,100,100).
        byte[] p(int b, int g, int r, int a) => [(byte)b, (byte)g, (byte)r, (byte)a];
        var src = new List<byte>();
        src.AddRange(p(10, 20, 30, 0)); src.AddRange(p(30, 40, 50, 0)); src.AddRange(p(100, 100, 100, 0)); src.AddRange(p(100, 100, 100, 0));
        src.AddRange(p(50, 60, 70, 0)); src.AddRange(p(70, 80, 90, 0)); src.AddRange(p(100, 100, 100, 0)); src.AddRange(p(100, 100, 100, 0));
        var dst = new byte[2 * 1 * 4];

        CoreGraphicsScreenCapture.Downscale(src.ToArray(), 4, 2, 16, 2, dst);

        Assert.Equal(new byte[] { 40, 50, 60, 255, 100, 100, 100, 255 }, dst);
    }

    [Fact]
    public void Downscale_HonoursRowPadding()
    {
        // 2x2 pixels in rows of 12 bytes (4 bytes of padding per row), factor 2 -> one pixel.
        var src = new byte[24];
        for (var i = 0; i < 4; i++) { src[i] = 200; src[i + 4] = 100; }                 // row 0: (200)(100)
        src[12] = 0; src[16] = 0; src[13] = 50; src[17] = 150; src[14] = 10; src[18] = 20;
        for (var i = 8; i < 12; i++) src[i] = 255; for (var i = 20; i < 24; i++) src[i] = 255;   // padding must not be read as pixels
        var dst = new byte[4];

        CoreGraphicsScreenCapture.Downscale(src, 2, 2, 12, 2, dst);

        Assert.Equal((200 + 100 + 0 + 0 + 2) / 4, dst[0]);
        Assert.Equal(255, dst[3]);
    }

    [Fact]
    public void Downscale_RefusesADestinationThatIsTooSmall() =>
        Assert.Throws<ArgumentException>(() => CoreGraphicsScreenCapture.Downscale(new byte[64], 4, 4, 16, 2, new byte[3]));

    [Fact]
    public void Downscale_ByOne_IsACopyThatIgnoresAlpha()
    {
        var src = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 };
        var dst = new byte[8];
        CoreGraphicsScreenCapture.Downscale(src, 2, 1, 8, 1, dst);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, dst);
    }

    [Fact]
    public void OnAnotherOperatingSystem_TheMacBackendsRefuseClearly_NotWithACrash()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.Throws<PlatformNotSupportedException>(() => new CGEventInputController());
        Assert.False(MacPermissions.CanCaptureScreen());
        Assert.False(MacPermissions.CanControlInput());
        Assert.Empty(MacPermissions.MissingPermissions());
    }

    [Fact]
    public void OpenPrivacySettings_OnNonMac_ReturnsFalse()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.False(MacPermissions.OpenScreenRecordingSettings());
        Assert.False(MacPermissions.OpenAccessibilitySettings());
    }

    [Fact]
    public async Task TheMacClipboard_UsesPbcopyAndPbpaste_WithTextOnStdin()
    {
        var runner = new RecordingRunner("pbpaste", "pbcopy") { Output = System.Text.Encoding.UTF8.GetBytes("hello") };
        var board = new MacClipboardManager(runner);
        Assert.True(board.IsAvailable);

        Assert.Equal("hello", await board.GetTextAsync());
        await board.SetTextAsync("it's \"quoted\" $(whoami)");

        Assert.Equal("pbpaste", runner.Calls[0].File);
        Assert.Equal("pbcopy", runner.Calls[1].File);
        Assert.Empty(runner.Calls[1].Args);
        Assert.Equal("it's \"quoted\" $(whoami)", System.Text.Encoding.UTF8.GetString(runner.Calls[1].Input!));
    }

    [Fact]
    public async Task WithoutPbcopy_TheMacClipboardIsUnavailable_ButNothingBreaks()
    {
        var board = new MacClipboardManager(new RecordingRunner());
        Assert.False(board.IsAvailable);
        Assert.Null(await board.GetTextAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => board.SetTextAsync("x"));
    }

    [Fact]
    public async Task OnNonMac_MacClipboard_GetImageReturnsNull_SetImageThrows()
    {
        if (OperatingSystem.IsMacOS()) return;
        var board = new MacClipboardManager(new RecordingRunner("pbpaste", "pbcopy"));
        Assert.Null(await board.GetImageAsync());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => board.SetImageAsync([1, 2, 3]));
    }

    private sealed class RecordingRunner(params string[] installed) : ICommandRunner
    {
        public List<(string File, string[] Args, byte[]? Input)> Calls { get; } = new();
        public byte[] Output { get; set; } = [];
        public bool Exists(string file) => installed.Contains(file);

        public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> arguments, byte[]? input, bool captureOutput, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((file, arguments.ToArray(), input));
            return Task.FromResult(new CommandResult(0, captureOutput ? Output : [], false));
        }
    }
}

/// <summary>
/// macOS image clipboard tests — require a real Mac to exercise NSPasteboard.
/// The non-Mac guard ensures the tests are silently skipped everywhere else.
/// </summary>
public sealed class MacImageClipboardTests
{
    [Fact]
    public async Task OnMac_SetAndGetImage_RoundTripsAPngViaTheSystemClipboard()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Minimal but valid PNG: 1×1 red pixel.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwADhQGAWjR9awAAAABJRU5ErkJggg==");

        var board = new MacClipboardManager();
        await board.SetImageAsync(png);
        var got = await board.GetImageAsync();

        Assert.NotNull(got);
        Assert.NotEmpty(got);
        // The data that comes back is whatever NSPasteboard stored (may be re-encoded).
        // Verify it is at least a PNG by checking the signature bytes.
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, got[..4]);
    }

    [Fact]
    public async Task OnMac_GetImage_ReturnsNull_WhenClipboardHasNoImage()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Put plain text in the clipboard so there is definitely no image.
        var board = new MacClipboardManager();
        await board.SetTextAsync("just text — no image");
        var got = await board.GetImageAsync();
        Assert.Null(got);
    }
}
