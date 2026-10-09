using System.Runtime.InteropServices;
using RemoteDesktop.Capture;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Input;
using RemoteDesktop.Platform.Windows;

namespace RemoteDesktop.Platform.Windows.Tests;

/// <summary>
/// The real Windows backends against the real desktop. They need an interactive session, so each test returns
/// early elsewhere (CI on Linux, a service session with no screen). Tests that touch shared state — the
/// clipboard, the mouse cursor — put it back the way they found it.
/// </summary>
public sealed class WindowsBackendTests
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private static bool HasDesktop() => OperatingSystem.IsWindows() && GetSystemMetrics(0) > 0 && GetSystemMetrics(1) > 0;

    // ── Screen capture ───────────────────────────────────────────────────

    [Fact]
    public async Task Capture_ProducesOpaqueFullScreenFrames_AtRoughlyTheRequestedRate()
    {
        if (!HasDesktop()) return;
        await using var capture = new GdiScreenCapture(fps: 20);
        await capture.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var frames = 0;
        var started = DateTime.UtcNow;
        await foreach (var f in capture.GetFramesAsync(cts.Token))
        {
            using (f)
            {
                Assert.Equal(GetSystemMetrics(0), f.Width);
                Assert.Equal(GetSystemMetrics(1), f.Height);
                Assert.Equal(f.Width * f.Height * 4, f.PixelData.Length);
                Assert.NotNull(f.Owner); // pooled: the consumer must dispose it
                Assert.Equal(frames, f.FrameNumber);

                // BGRA, opaque everywhere (alpha forced to 255), and the screen is not all black.
                var span = f.PixelData.Span;
                var anyColor = false;
                for (var i = 3; i < span.Length; i += 4 * 997)
                {
                    Assert.Equal(255, span[i]);
                    anyColor |= span[i - 1] != 0 || span[i - 2] != 0 || span[i - 3] != 0;
                }
                if (frames == 0) Assert.True(anyColor, "captured frame is entirely black");
            }
            if (++frames == 10) break;
        }
        await capture.StopAsync();

        Assert.Equal(10, frames);
        var elapsed = DateTime.UtcNow - started;
        Assert.InRange(elapsed.TotalSeconds, 0.3, 3.0); // 10 frames at 20 fps ≈ 0.5 s
    }

    [Fact]
    public async Task Capture_StopsWhenStopped()
    {
        if (!HasDesktop()) return;
        await using var capture = new GdiScreenCapture(fps: 30);
        await capture.StartAsync();
        var count = 0;
        await foreach (var f in capture.GetFramesAsync())
        {
            f.Dispose();
            if (++count == 3) await capture.StopAsync();
        }
        Assert.Equal(3, count);
        Assert.False(capture.IsCapturing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(61)]
    public void Capture_RejectsSillyFrameRates(int fps)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new GdiScreenCapture(fps));
    }

    // ── Mouse ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mouse_Move_PutsTheCursorWhereTheViewerPointed_ThenRestoresIt()
    {
        if (!HasDesktop()) return;
        GetCursorPos(out var original);
        try
        {
            var controller = new SendInputController();
            // Normalized centre of the screen.
            await controller.InjectAsync(new InputEvent { EventType = InputEventType.MouseMove, X = 16383, Y = 16383 });

            // The cursor must reach the centre. Poll rather than sleep: on a live desktop a real mouse can nudge
            // it right after, and what matters is that the injected move took effect.
            int cx = GetSystemMetrics(0) / 2, cy = GetSystemMetrics(1) / 2;
            var reached = false;
            for (var i = 0; i < 50 && !reached; i++)
            {
                GetCursorPos(out var now);
                reached = Math.Abs(now.X - cx) <= 3 && Math.Abs(now.Y - cy) <= 3;
                if (!reached) await Task.Delay(10);
            }
            Assert.True(reached, "the cursor never reached the injected position");
            Assert.Equal(0, controller.FailedInjections);
        }
        finally { SetCursorPos(original.X, original.Y); }
    }

    [Fact]
    public async Task Injecting_AnUnknownButton_IsANoOp_NotAnError()
    {
        if (!HasDesktop()) return;
        var controller = new SendInputController();
        await controller.InjectAsync(new InputEvent { EventType = InputEventType.MouseButtonDown, X = 1, Y = 1, Button = 9 });
        Assert.Equal(0, controller.FailedInjections);
    }

    // ── Clipboard ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hello")]
    [InlineData("多行\r\n中文 ✓ 😀")]
    [InlineData("")]
    public async Task Clipboard_Text_RoundTrips(string text)
    {
        if (!HasDesktop()) return;
        var clipboard = new WindowsClipboardManager();
        var original = await clipboard.GetTextAsync();
        try
        {
            await clipboard.SetTextAsync(text);
            Assert.Equal(text, await clipboard.GetTextAsync());
        }
        finally
        {
            if (original is not null) await clipboard.SetTextAsync(original);
        }
    }

    [Fact]
    public async Task Clipboard_IsOffByDefault_AndImagesAreReportedUnsupported()
    {
        if (!OperatingSystem.IsWindows()) return;
        var clipboard = new WindowsClipboardManager();
        Assert.Equal(ClipboardPolicy.Disabled, clipboard.Policy);
        Assert.Null(await clipboard.GetImageAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => clipboard.SetImageAsync([1, 2, 3]));
    }
}
