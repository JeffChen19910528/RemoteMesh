using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Input;

namespace RemoteDesktop.Platform.Windows;

/// <summary>
/// Injects input with SendInput. Best effort by design: Windows refuses to inject into windows of a higher
/// integrity level (an elevated app, UAC prompt) and an exception here would kill the whole input channel, so a
/// refusal is counted in <see cref="FailedInjections"/> instead of thrown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SendInputController : IInputController
{
    private static readonly int InputSize = Marshal.SizeOf<Native.INPUT>();
    private long _failed;

    public SendInputController() => DpiAwareness.Ensure();

    /// <summary>How many injections Windows refused (for diagnostics / UI).</summary>
    public long FailedInjections => Interlocked.Read(ref _failed);

    public Task InjectAsync(InputEvent inputEvent, CancellationToken ct = default)
    {
        var specs = InputTranslator.Translate(inputEvent, vk => (ushort)Native.MapVirtualKeyW(vk, Native.MAPVK_VK_TO_VSC));
        if (specs.Count == 0) return Task.CompletedTask;

        var inputs = new Native.INPUT[specs.Count];
        for (var i = 0; i < specs.Count; i++) inputs[i] = ToNative(specs[i]);

        var sent = Native.SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length) Interlocked.Add(ref _failed, inputs.Length - sent);
        return Task.CompletedTask;
    }

    private static Native.INPUT ToNative(InputSpec s) => s.IsKey
        ? new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = s.VirtualKey, wScan = s.ScanCode, dwFlags = s.Flags } }
        }
        : new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = s.X, dy = s.Y, mouseData = s.MouseData, dwFlags = s.Flags } }
        };
}
