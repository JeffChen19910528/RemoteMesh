using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.Encoding;
using RemoteDesktop.FileTransfer;
using RemoteDesktop.Input;
using RemoteDesktop.Protocol;
using RemoteDesktop.Streaming;

namespace RemoteDesktop.Client;

/// <summary>
/// The controlling machine's side of a session: it shows the remote screen and sends input, clipboard and file
/// requests. The host decides what is actually allowed; requests beyond the granted rights are refused there.
/// </summary>
public sealed class ViewerSession : SessionBase
{
    private readonly DesktopStreamReceiver _receiver;
    private readonly InputSender _input;
    private readonly byte[] _supported;

    internal ViewerSession(SessionId id, SessionEndpoint endpoint, ClientOptions options, Permission granted,
        IFrameRenderer renderer, Func<SessionId, string, Task>? closeOnServer)
        : base(id, endpoint, options, closeOnServer)
    {
        Granted = granted;
        // Every codec this computer can decode; the host picks the best one both sides have.
        var decoders = new List<IVideoDecoder> { new TileDeltaVideoDecoder() };
        if (options.JpegCodec is { } jpeg) decoders.Add(new TileDeltaJpegDecoder(jpeg));
        _supported = decoders.Select(d => (byte)d.Codec).ToArray();
        _receiver = new DesktopStreamReceiver(endpoint[ChannelType.Desktop], decoders, renderer);
        _input = new InputSender(endpoint[ChannelType.Input]);
        Clipboard = new ClipboardClient(endpoint[ChannelType.Clipboard]);
        Files = new FileTransferClient(endpoint[ChannelType.File]);
    }

    /// <summary>What the host granted. Informational: the host enforces it.</summary>
    public Permission Granted { get; }

    public ClipboardClient Clipboard { get; }
    public FileTransferClient Files { get; }
    public long FramesRendered => _receiver.FramesRendered;

    /// <summary>Encoded video bytes received so far.</summary>
    public long VideoBytesReceived => _receiver.BytesReceived;

    /// <summary>The video codec the host is using (known once the first frame arrived).</summary>
    public CodecType? VideoCodec => _receiver.LastCodec;

    /// <summary>Sends one input event (coordinates normalized 0–32767 over the host's primary screen).</summary>
    public Task SendInputAsync(InputEvent e, CancellationToken ct = default) => _input.SendAsync(e, ct);

    // Frames lost during the outage leave the picture stale; ask for a whole new one.
    protected override void OnReattached() =>
        _ = Endpoint[ChannelType.Control].SendAsync(new[] { ControlTags.KeyframeRequest }, Token);

    internal void Start()
    {
        // A lost or corrupt frame makes the decoder wait for a keyframe; ask the host for one right away.
        _receiver.KeyframeNeeded += () =>
            _ = Endpoint[ChannelType.Control].SendAsync(new[] { ControlTags.KeyframeRequest }, Token);

        // Tell the host which codecs we can decode before it starts sending.
        var hello = new byte[1 + _supported.Length];
        hello[0] = ControlTags.Hello;
        _supported.CopyTo(hello, 1);
        Run("hello", ct => Endpoint[ChannelType.Control].SendAsync(hello, ct));

        Run("screen", ct => _receiver.RunAsync(ct));
        Run("clipboard", ct => Clipboard.RunAsync(ct));
        Run("file transfer", ct => Files.RunAsync(ct));
        StartKeepAliveAndWatchdog();
    }
}
