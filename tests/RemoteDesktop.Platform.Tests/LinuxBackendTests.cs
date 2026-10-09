using System.Runtime.InteropServices;
using RemoteDesktop.Capture;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Input;
using RemoteDesktop.Platform;
using RemoteDesktop.Platform.Linux;

namespace RemoteDesktop.Platform.Tests;

/// <summary>Runs only on Linux with an X server (DISPLAY set), for example under Xvfb.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) Skip = "Needs an X server (DISPLAY).";
    }
}

public sealed class X11KeyMapTests
{
    [Theory]
    [InlineData(0x41, 0x61)]   // A -> a
    [InlineData(0x5A, 0x7A)]   // Z -> z
    [InlineData(0x30, 0x30)]   // 0
    [InlineData(0x39, 0x39)]   // 9
    [InlineData(0x70, 0xFFBE)] // F1
    [InlineData(0x7B, 0xFFC9)] // F12
    [InlineData(0x0D, 0xFF0D)] // Enter
    [InlineData(0x1B, 0xFF1B)] // Esc
    [InlineData(0x25, 0xFF51)] // Left
    [InlineData(0x28, 0xFF54)] // Down
    [InlineData(0x2E, 0xFFFF)] // Delete
    [InlineData(0x10, 0xFFE1)] // Shift
    [InlineData(0xA3, 0xFFE4)] // Right Ctrl
    [InlineData(0x5B, 0xFFEB)] // Windows key -> Super
    [InlineData(0x60, 0xFFB0)] // numpad 0
    [InlineData(0xBA, 0x3B)]   // ;
    [InlineData(0xC0, 0x60)]   // `
    public void WindowsKeys_BecomeTheRightKeysyms(int vk, uint keysym) => Assert.Equal(keysym, X11KeyMap.ToKeysym(vk));

    [Theory]
    [InlineData(0)]
    [InlineData(0x07)]
    [InlineData(0xFF)]
    [InlineData(-5)]
    [InlineData(100000)]
    public void UnknownKeys_MapToNothing(int vk) => Assert.Equal(0u, X11KeyMap.ToKeysym(vk));

    [Fact]
    public void EveryLetterAndDigitAndFunctionKey_HasAKeysym()
    {
        for (var vk = 0x30; vk <= 0x39; vk++) Assert.NotEqual(0u, X11KeyMap.ToKeysym(vk));
        for (var vk = 0x41; vk <= 0x5A; vk++) Assert.NotEqual(0u, X11KeyMap.ToKeysym(vk));
        for (var vk = 0x70; vk <= 0x87; vk++) Assert.NotEqual(0u, X11KeyMap.ToKeysym(vk));
        for (var vk = 0x60; vk <= 0x69; vk++) Assert.NotEqual(0u, X11KeyMap.ToKeysym(vk));
    }

    [Fact]
    public void NoTwoWindowsKeysShareAKeysym_ExceptTheLeftAndRightPairsSentAsGeneric()
    {
        var seen = new Dictionary<uint, int>();
        for (var vk = 1; vk < 256; vk++)
        {
            var sym = X11KeyMap.ToKeysym(vk);
            if (sym == 0) continue;
            if (seen.TryGetValue(sym, out var other))
                Assert.True((other, vk) is (0x10, 0xA0) or (0x11, 0xA2) or (0x12, 0xA4), $"VK {other:X} and {vk:X} both map to {sym:X}");
            seen[sym] = vk;
        }
    }

    [Theory]
    [InlineData(1, 1u)] // left
    [InlineData(2, 3u)] // right is X button 3
    [InlineData(3, 2u)] // middle is X button 2
    [InlineData(4, 0u)]
    [InlineData(0, 0u)]
    public void MouseButtons_FollowTheXNumbering(int wire, uint x) => Assert.Equal(x, X11KeyMap.ToButton(wire));

    [Theory]
    [InlineData(120, 4u, 1)]
    [InlineData(-120, 5u, 1)]
    [InlineData(360, 4u, 3)]
    [InlineData(30, 4u, 1)]    // a small nudge still moves one step
    [InlineData(-5000, 5u, 20)] // and a huge one is capped
    public void TheWheel_BecomesButtonClicks(int delta, uint button, int clicks) => Assert.Equal((button, clicks), X11KeyMap.ToWheel(delta));

    [Fact]
    public void ZeroWheel_DoesNothing() => Assert.Equal((0u, 0), X11KeyMap.ToWheel(0));

    [Theory]
    [InlineData(0, 1920, 0)]
    [InlineData(32767, 1920, 1919)]
    [InlineData(16384, 1920, 960)]
    [InlineData(-10, 1920, 0)]
    [InlineData(99999, 1920, 1919)]
    [InlineData(100, 1, 0)]
    public void NormalisedCoordinates_LandInsideTheScreen(int normalized, int size, int pixel) => Assert.Equal(pixel, X11KeyMap.ToPixel(normalized, size));
}

public sealed class LinuxClipboardLogicTests
{
    private sealed class FakeRunner(params string[] installed) : ICommandRunner
    {
        public List<(string File, string[] Args, byte[]? Input)> Calls { get; } = new();
        public byte[] Output { get; set; } = [];
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public bool Exists(string file) => installed.Contains(file);

        public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> arguments, byte[]? input, bool captureOutput, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((file, arguments.ToArray(), input));
            return Task.FromResult(new CommandResult(ExitCode, captureOutput ? Output : [], TimedOut));
        }
    }

    [Fact]
    public void WaylandPrefersWlClipboard_X11PrefersXclip_ThenXsel()
    {
        Assert.Equal("wl-paste", WhichRead(new FakeRunner("wl-paste", "wl-copy", "xclip"), wayland: true));
        Assert.Equal("xclip", WhichRead(new FakeRunner("wl-paste", "wl-copy", "xclip"), wayland: false));
        Assert.Equal("xsel", WhichRead(new FakeRunner("xsel"), wayland: false));
        Assert.Equal("xclip", WhichRead(new FakeRunner("xclip"), wayland: true)); // Wayland session but only X tools: XWayland bridges them
    }

    private static string WhichRead(FakeRunner runner, bool wayland)
    {
        var board = new LinuxClipboardManager(runner, wayland);
        runner.Output = [1];
        board.GetTextAsync().Wait();
        return runner.Calls.Single().File;
    }

    [Fact]
    public async Task WithoutAnyTool_TheClipboardIsUnavailable_ReadsAreEmpty_WritesRefuse()
    {
        var board = new LinuxClipboardManager(new FakeRunner(), wayland: false);
        Assert.False(board.IsAvailable);
        Assert.Null(await board.GetTextAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => board.SetTextAsync("x"));
    }

    [Fact]
    public async Task Text_IsPassedAsInput_NotAsAnArgument_SoItCanNeverBecomeACommand()
    {
        var runner = new FakeRunner("xclip");
        var board = new LinuxClipboardManager(runner, wayland: false);
        var nasty = "'; rm -rf ~; echo \"$(whoami)\" `id` \n--help";

        await board.SetTextAsync(nasty);

        var call = Assert.Single(runner.Calls);
        Assert.DoesNotContain(call.Args, a => a.Contains("rm"));
        Assert.Equal(nasty, System.Text.Encoding.UTF8.GetString(call.Input!));
    }

    [Fact]
    public async Task AnEmptyClipboard_ATimeout_AndAFailingTool_AreAllJustNull()
    {
        var runner = new FakeRunner("xclip") { ExitCode = 1 };
        var board = new LinuxClipboardManager(runner, wayland: false);
        Assert.Null(await board.GetTextAsync());
        runner.ExitCode = 0; runner.TimedOut = true; runner.Output = [65];
        Assert.Null(await board.GetTextAsync());
        runner.TimedOut = false; runner.Output = [];
        Assert.Null(await board.GetTextAsync());
    }

    [Fact]
    public async Task AWriteTheToolRefused_IsReported_NotSwallowed()
    {
        var runner = new FakeRunner("xclip") { ExitCode = 1 };
        await Assert.ThrowsAsync<IOException>(() => new LinuxClipboardManager(runner, wayland: false).SetTextAsync("x"));
        runner.ExitCode = 0; runner.TimedOut = true;
        await Assert.ThrowsAsync<IOException>(() => new LinuxClipboardManager(runner, wayland: false).SetTextAsync("x"));
    }

    [Fact]
    public async Task OversizedText_IsNeitherReadNorWritten()
    {
        var runner = new FakeRunner("xclip") { Output = new byte[CommandClipboardManager.MaxTextBytes + 1] };
        var board = new LinuxClipboardManager(runner, wayland: false);
        Assert.Null(await board.GetTextAsync());
        await Assert.ThrowsAsync<IOException>(() => board.SetTextAsync(new string('x', CommandClipboardManager.MaxTextBytes + 1)));
        Assert.Single(runner.Calls); // the write never reached the tool
    }

    [Fact]
    public async Task TextIsUtf8_BothWays()
    {
        var runner = new FakeRunner("xclip") { Output = System.Text.Encoding.UTF8.GetBytes("你好 🌏 café") };
        var board = new LinuxClipboardManager(runner, wayland: false);
        Assert.Equal("你好 🌏 café", await board.GetTextAsync());
        await board.SetTextAsync("日本語");
        Assert.Equal("日本語", System.Text.Encoding.UTF8.GetString(runner.Calls[1].Input!));
    }

    [Fact]
    public async Task ImagesAreNotSupportedYet_AndSharingStartsDisabled()
    {
        var board = new LinuxClipboardManager(new FakeRunner("xclip"), wayland: false);
        Assert.Equal(ClipboardPolicy.Disabled, board.Policy);
        Assert.Null(await board.GetImageAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => board.SetImageAsync([1]));
    }
}

/// <summary>The real thing: an X server, real windows, real clipboard tools. Skipped anywhere else.</summary>
public sealed class LinuxRealBackendTests
{
    private const string X11 = "libX11.so.6";
    [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] private static extern int XCloseDisplay(IntPtr d);
    [DllImport(X11)] private static extern IntPtr XDefaultRootWindow(IntPtr d);
    [DllImport(X11)] private static extern IntPtr XCreateSimpleWindow(IntPtr d, IntPtr parent, int x, int y, uint w, uint h, uint border, UIntPtr borderColor, UIntPtr background);
    [DllImport(X11)] private static extern int XMapWindow(IntPtr d, IntPtr w);
    [DllImport(X11)] private static extern int XDestroyWindow(IntPtr d, IntPtr w);
    [DllImport(X11)] private static extern int XSync(IntPtr d, int discard);
    [DllImport(X11)] private static extern int XQueryPointer(IntPtr d, IntPtr w, out IntPtr rootReturn, out IntPtr childReturn, out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [DllImport(X11)] private static extern int XQueryKeymap(IntPtr d, byte[] keys);
    [DllImport(X11)] private static extern uint XKeysymToKeycode(IntPtr d, UIntPtr keysym);

    private static async Task<CaptureFrame> FirstFrameAsync(int fps = 30)
    {
        await using var capture = new X11ScreenCapture(fps);
        await capture.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var f in capture.GetFramesAsync(cts.Token)) return f;
        throw new InvalidOperationException("no frame");
    }

    [LinuxFact]
    public async Task TheScreenIsCaptured_AsOpaqueBgra_WithAWindowWhereItWasPut()
    {
        var d = XOpenDisplay(IntPtr.Zero);
        var root = XDefaultRootWindow(d);
        var win = XCreateSimpleWindow(d, root, 50, 60, 200, 100, 0, UIntPtr.Zero, (UIntPtr)0xFF0000u); // red
        XMapWindow(d, win);
        XSync(d, 0);
        await Task.Delay(300);
        try
        {
            using var frame = await FirstFrameAsync();
            Assert.True(frame.Width >= 640 && frame.Height >= 480);
            Assert.Equal(frame.Width * frame.Height * 4, frame.PixelData.Length);

            var px = frame.PixelData.ToArray();
            int At(int x, int y, int c) => px[(y * frame.Width + x) * 4 + c];
            // BGRA: the window is pure red, and everything about it is opaque.
            Assert.Equal((0, 0, 255, 255), (At(100, 100, 0), At(100, 100, 1), At(100, 100, 2), At(100, 100, 3)));
            Assert.NotEqual(255, At(frame.Width - 5, frame.Height - 5, 2));   // elsewhere it is not red
            for (var i = 3; i < px.Length; i += 4 * 997) Assert.Equal(255, px[i]);
        }
        finally { XDestroyWindow(d, win); XCloseDisplay(d); }
    }

    [LinuxFact]
    public async Task ARealScreenshot_SurvivesTheWholeLossyPipeline_WithItsColours()
    {
        var d = XOpenDisplay(IntPtr.Zero);
        var root = XDefaultRootWindow(d);
        var win = XCreateSimpleWindow(d, root, 100, 100, 300, 200, 0, UIntPtr.Zero, (UIntPtr)0xFF0000u); // red
        XMapWindow(d, win);
        XSync(d, 0);
        await Task.Delay(300);
        try
        {
            using var frame = await FirstFrameAsync();
            var codec = new RemoteDesktop.Platform.Skia.SkiaJpegCodec();
            var enc = new RemoteDesktop.Encoding.TileDeltaJpegEncoder(codec)
            { Quality = new RemoteDesktop.Encoding.VideoQuality { Width = 0, Height = 0, Fps = 20, BitrateKbps = 4000 } };
            var dec = new RemoteDesktop.Encoding.TileDeltaJpegDecoder(codec);

            using var back = await dec.DecodeAsync(await enc.EncodeAsync(frame));

            var px = back.PixelData.ToArray();
            int At(int x, int y, int c) => px[(y * back.Width + x) * 4 + c];
            Assert.Equal((frame.Width, frame.Height), (back.Width, back.Height));
            Assert.True(At(200, 200, 2) > 240 && At(200, 200, 1) < 20 && At(200, 200, 0) < 20, $"inside the window: B={At(200, 200, 0)} G={At(200, 200, 1)} R={At(200, 200, 2)}");
            Assert.True(At(900, 600, 2) < 20 && At(900, 600, 1) < 20 && At(900, 600, 0) < 20, "outside the window it is black");
        }
        finally { XDestroyWindow(d, win); XCloseDisplay(d); }
    }

    private sealed class FrameCounter : RemoteDesktop.Streaming.IFrameRenderer
    {
        public List<(int NonBlack, int Red)> Frames { get; } = new();
        public Task RenderAsync(CaptureFrame f, CancellationToken ct = default)
        {
            var span = f.PixelData.Span;
            int nb = 0, red = 0;
            for (var i = 0; i + 3 < f.Width * f.Height * 4; i += 4)
            {
                if (span[i] != 0 || span[i + 1] != 0 || span[i + 2] != 0) nb++;
                if (span[i + 2] > 200 && span[i + 1] < 60 && span[i] < 60) red++;
            }
            lock (Frames) Frames.Add((nb, red));
            return Task.CompletedTask;
        }
    }

    [LinuxFact]
    public async Task TheStreamedScreen_StaysCorrect_FrameAfterFrame_WithTheRateController()
    {
        var d = XOpenDisplay(IntPtr.Zero);
        var root = XDefaultRootWindow(d);
        var win = XCreateSimpleWindow(d, root, 100, 100, 300, 200, 0, UIntPtr.Zero, (UIntPtr)0xFF0000u);
        XMapWindow(d, win);
        XSync(d, 0);
        await Task.Delay(300);
        try
        {
            var codec = new RemoteDesktop.Platform.Skia.SkiaJpegCodec();
            var transport = new RemoteDesktop.Transport.LoopbackTransport(RemoteDesktop.Core.SessionId.New());
            var channel = await transport.OpenChannelAsync(RemoteDesktop.Protocol.ChannelType.Desktop);
            var renderer = new FrameCounter();
            var sender = new RemoteDesktop.Streaming.DesktopStreamSender(new X11ScreenCapture(20),
                new RemoteDesktop.Encoding.TileDeltaJpegEncoder(codec), channel, new RemoteDesktop.Encoding.RateController(6000));
            var receiver = new RemoteDesktop.Streaming.DesktopStreamReceiver(channel, new RemoteDesktop.Encoding.TileDeltaJpegDecoder(codec), renderer);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var send = sender.RunAsync(cts.Token);
            var receive = receiver.RunAsync(cts.Token);
            await Task.WhenAll(send, receive).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotEmpty(renderer.Frames);
            Assert.All(renderer.Frames, f => Assert.True(f.Red >= 55_000, $"a frame lost the window: {renderer.Frames.IndexOf(f)}: nonBlack={f.NonBlack} red={f.Red}"));
        }
        finally { XDestroyWindow(d, win); XCloseDisplay(d); }
    }

    [LinuxFact]
    public async Task FramesKeepComingAtAboutTheRequestedRate_AndTheMemoryGoesBackToThePool()
    {
        await using var capture = new X11ScreenCapture(fps: 20);
        await capture.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var n = 0;
        var start = DateTime.UtcNow;
        await foreach (var f in capture.GetFramesAsync(cts.Token))
        {
            f.Dispose();
            if (++n == 20) break;
        }
        var seconds = (DateTime.UtcNow - start).TotalSeconds;
        Assert.InRange(seconds, 0.7, 3.0); // 20 frames at 20 fps: about a second
    }

    [LinuxFact]
    public async Task StoppingTheCapture_EndsTheStream()
    {
        await using var capture = new X11ScreenCapture(30);
        await capture.StartAsync();
        var n = 0;
        await foreach (var f in capture.GetFramesAsync())
        {
            f.Dispose();
            if (++n == 3) await capture.StopAsync();
        }
        Assert.False(capture.IsCapturing);
    }

    [LinuxFact]
    public async Task TheMouseMoves_ToTheNormalisedPosition()
    {
        using var input = new XTestInputController();
        var d = XOpenDisplay(IntPtr.Zero);
        try
        {
            var root = XDefaultRootWindow(d);
            await input.InjectAsync(new InputEvent { EventType = InputEventType.MouseMove, X = 16384, Y = 8192 });
            await Task.Delay(100);
            XQueryPointer(d, root, out _, out _, out var x, out var y, out _, out _, out _);

            using var frame = await FirstFrameAsync();
            Assert.InRange(x, frame.Width / 2 - 2, frame.Width / 2 + 2);   // 16384/32768 = the middle
            Assert.InRange(y, frame.Height / 4 - 2, frame.Height / 4 + 2); // 8192/32768 = a quarter down
            Assert.Equal(0, input.FailedInjections);
        }
        finally { XCloseDisplay(d); }
    }

    [LinuxFact]
    public async Task AKeyIsHeldWhileItIsDown_AndReleasedWhenItGoesUp()
    {
        using var input = new XTestInputController();
        var d = XOpenDisplay(IntPtr.Zero);
        try
        {
            var keycode = XKeysymToKeycode(d, (UIntPtr)0x61u); // 'a'
            bool Down()
            {
                var map = new byte[32];
                XQueryKeymap(d, map);
                return (map[keycode / 8] & (1 << (int)(keycode % 8))) != 0;
            }

            await input.InjectAsync(new InputEvent { EventType = InputEventType.KeyDown, KeyCode = 0x41 }); // VK 'A'
            await Task.Delay(100);
            Assert.True(Down());
            await input.InjectAsync(new InputEvent { EventType = InputEventType.KeyUp, KeyCode = 0x41 });
            await Task.Delay(100);
            Assert.False(Down());
            Assert.Equal(0, input.FailedInjections);
        }
        finally { XCloseDisplay(d); }
    }

    [LinuxFact]
    public async Task UnknownKeysAndButtons_AreIgnored_NotFatal()
    {
        using var input = new XTestInputController();
        await input.InjectAsync(new InputEvent { EventType = InputEventType.KeyDown, KeyCode = 0x07 });
        await input.InjectAsync(new InputEvent { EventType = InputEventType.MouseButtonDown, Button = 9, X = 100, Y = 100 });
        await input.InjectAsync(new InputEvent { EventType = InputEventType.MouseWheel, Delta = 240 });
        await input.InjectAsync(new InputEvent { EventType = InputEventType.MouseButtonDown, Button = 1, X = 100, Y = 100 });
        await input.InjectAsync(new InputEvent { EventType = InputEventType.MouseButtonUp, Button = 1, X = 100, Y = 100 });
        Assert.Equal(0, input.FailedInjections);
    }

    [LinuxFact]
    public async Task AfterDispose_InjectingDoesNothing_InsteadOfCrashing()
    {
        var input = new XTestInputController();
        input.Dispose();
        input.Dispose();
        await input.InjectAsync(new InputEvent { EventType = InputEventType.MouseMove, X = 1, Y = 1 });
    }

    [LinuxFact]
    public async Task TheRealClipboard_RoundTripsText_WithQuotesNewlinesAndUnicode()
    {
        var board = new LinuxClipboardManager(wayland: false);
        if (!board.IsAvailable) return; // no xclip/xsel here: nothing to test
        var text = "line one\n'quoted' \"double\" $HOME `id`; 你好 🌏\r\nend";

        await board.SetTextAsync(text);
        Assert.Equal(text, await board.GetTextAsync());

        await board.SetTextAsync("second");
        Assert.Equal("second", await board.GetTextAsync());
    }
}
