using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections.Client;
using RemoteDesktop.Capture;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.Encoding;
using RemoteDesktop.Input;

namespace RemoteDesktop.Client;

public sealed class ClientOptions
{
    /// <summary>Base address of the server, for example <c>https://relay.example.com</c>.</summary>
    public required Uri ServerUri { get; init; }

    public string DeviceName { get; init; } = Environment.MachineName;

    /// <summary>Where the device identity and key live. Null = the per-user default location.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>
    /// A JPEG implementation for lossy video (Windows: <c>GdiJpegCodec</c>). Without one the client only speaks the
    /// lossless codec, which is correct but needs a lot more bandwidth when the screen changes a lot.
    /// </summary>
    public IJpegCodec? JpegCodec { get; init; }

    /// <summary>The most bandwidth the video should use when this computer is the one being watched (kilobits per second).</summary>
    public int VideoBudgetKbps { get; init; } = 6000;

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a session whose connection dropped keeps trying to come back before it is ended. The server forgets a
    /// dropped session after its own recovery window (a minute by default), so more than that only helps when just the
    /// relay connection was lost.
    /// </summary>
    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// When the connection is lost but this computer's link to the server is fine, the other side may be the one that
    /// dropped and will rebuild the session itself. Wait this long for it before doing it from here. (The side that
    /// asked for the session waits this long; the controlled side waits twice as long, so they do not both do it.)
    /// </summary>
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Hook for the signaling connection (proxy, custom handler, transport). Tests use it.</summary>
    public Action<HttpConnectionOptions>? ConfigureHub { get; init; }

    /// <summary>Opens the relay WebSocket for (uri, relay token). Null = a normal ClientWebSocket.</summary>
    public Func<Uri, string, CancellationToken, Task<WebSocket>>? RelayConnector { get; init; }

    internal Uri HubUri => new(ServerUri, "/hubs/signaling");

    internal Uri RelayUri(SessionId session, DeviceId device)
    {
        var b = new UriBuilder(new Uri(ServerUri, "/ws/relay"))
        {
            Scheme = ServerUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = $"sessionId={session}&deviceId={device}"
        };
        return b.Uri;
    }
}

/// <summary>What the host machine offers to a remote viewer. Anything left null is simply unavailable.</summary>
public sealed class HostServices
{
    /// <summary>Creates a fresh screen source per session (it is started and stopped with the session).</summary>
    public Func<IScreenCapture>? CaptureFactory { get; init; }

    public IInputController? Input { get; init; }
    public IClipboardManager? Clipboard { get; init; }

    /// <summary>The only folder remote file transfers can touch. Null = file transfer unavailable.</summary>
    public string? FileRoot { get; init; }
}

/// <summary>Who is asking to connect (shown to the person at the host so they can decide).</summary>
public sealed record ApprovalRequest(SessionId SessionId, DeviceId RequesterDeviceId, string? RequesterShortId = null);

/// <param name="Accept">Allow the connection.</param>
/// <param name="Granted">The rights to give. Least privilege: grant only what was agreed to.</param>
public sealed record ApprovalDecision(bool Accept, Permission Granted)
{
    public static ApprovalDecision Deny { get; } = new(false, Permission.None);
}

/// <summary>The host user's answer to "somebody wants to connect". Implemented by the UI.</summary>
public interface IApprovalHandler
{
    Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>Control-channel messages besides keep-alive.</summary>
internal static class ControlTags
{
    public const byte KeyframeRequest = 0xA0;

    /// <summary>[0xA1][codec:1]… — the viewer's list of video codecs it can decode, sent once when it starts.</summary>
    public const byte Hello = 0xA1;
}
