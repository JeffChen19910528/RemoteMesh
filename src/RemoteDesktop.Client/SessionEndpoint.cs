using System.Net.WebSockets;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Relay;
using RemoteDesktop.Security;
using RemoteDesktop.Signaling;
using RemoteDesktop.Transport;
using RemoteDesktop.Transport.Secure;

namespace RemoteDesktop.Client;

/// <summary>
/// One side of an established, end-to-end encrypted session: the relay connection, the derived key and the five
/// channels. Every channel goes through <see cref="SecureTransport"/>, so the relay only ever sees ciphertext.
/// The connection under the channels can be replaced (<see cref="ReattachAsync"/>) with a new relay connection and a
/// new key epoch while the channels, and so everything built on them, stay the same objects.
/// </summary>
internal sealed class SessionEndpoint : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly ISignalingClient _signaling;
    private readonly DeviceKeyPair _myKeys;
    private readonly DeviceId _me;
    private readonly byte[] _peerKey;
    private readonly PeerPinStore _pins;
    private readonly bool _initiator;
    private readonly Dictionary<ChannelType, ResilientChannel> _channels;
    private SecureTransport? _transport;
    private int _epoch;
    private int _disposed;

    private SessionEndpoint(ClientOptions options, ISignalingClient signaling, DeviceKeyPair myKeys, DeviceId me,
        SessionId sessionId, DeviceId peer, byte[] peerKey, PeerPinStore pins, bool initiator)
    {
        _options = options;
        _signaling = signaling;
        _myKeys = myKeys;
        _me = me;
        SessionId = sessionId;
        _peerKey = peerKey;
        _pins = pins;
        PeerDeviceId = peer;
        SecurityCode = KeyFingerprint.SecurityCode(myKeys.PublicKey, peerKey);
        _initiator = initiator;
        // Only input is dropped while the link is down; everything else waits for it to come back.
        _channels = Enum.GetValues<ChannelType>().ToDictionary(t => t, t => new ResilientChannel(t, dropWhileDown: t == ChannelType.Input));
    }

    public SessionId SessionId { get; }

    /// <summary>The device on the other end, as the server named it and as pinned on this machine.</summary>
    public DeviceId PeerDeviceId { get; }

    /// <summary>Six groups of five digits, the same on both screens; the two people compare them to rule out a man in the middle.</summary>
    public string SecurityCode { get; }

    /// <summary>True once the person marked this peer's key as checked (kept across sessions as long as the key stays the same).</summary>
    public bool PeerVerified => _pins.IsVerified(PeerDeviceId, _peerKey);

    /// <summary>True the first time this machine sees this peer (nothing to compare against yet).</summary>
    public bool PeerIsNew { get; private set; }

    /// <summary>The device changed its key since the last time, and proved with both keys that it was the one who did.</summary>
    public bool PeerKeyRotated { get; private set; }

    public bool MarkPeerVerified() => _pins.MarkVerified(PeerDeviceId, _peerKey);

    public IChannel this[ChannelType type] => _channels[type];

    /// <summary>The key epoch the current connection uses (0 at first, higher after every reconnect).</summary>
    public int Epoch => Volatile.Read(ref _epoch);

    public bool IsConnected => _transport is { IsConnected: true };

    /// <param name="initiator">True for the side that requested the session (it and the peer must differ).</param>
    public static async Task<SessionEndpoint> OpenAsync(
        ClientOptions options, ISignalingClient signaling, DeviceKeyPair myKeys, DeviceId me, PeerPinStore pins,
        SessionId sessionId, string relayToken, bool initiator, CancellationToken ct)
    {
        // The peer's public key comes from the server, which pinned it when the peer first registered. This machine
        // pins it too, on first sight: a key that differs from the pinned one stops the session before any key
        // agreement, so a server cannot swap the key later. (The first contact is still trust on first use; the
        // security code lets the two people check it.)
        var (peer, _, peerKey) = await signaling.GetPeerIdentityAsync(sessionId, ct);
        var pinned = pins.Check(peer, peerKey);
        var rotated = false;
        if (pinned == PeerPinResult.KeyChanged)
        {
            // The device may simply have changed its key on purpose. Only the device's own signatures count as proof.
            var history = await signaling.GetPeerKeyRotationsAsync(sessionId, peer, ct);
            if (!pins.TryFollowRotations(peer, peerKey, history)) throw new PeerKeyChangedException(peer);
            pinned = PeerPinResult.Known;
            rotated = true;
        }

        var endpoint = new SessionEndpoint(options, signaling, myKeys, me, sessionId, peer, peerKey, pins, initiator)
        {
            PeerIsNew = pinned == PeerPinResult.NewPeer,
            PeerKeyRotated = rotated
        };
        try
        {
            await endpoint.ConnectAsync(epoch: 0, relayToken, ct);
            return endpoint;
        }
        catch
        {
            await endpoint.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Replaces the connection with a new one at <paramref name="epoch"/>: a fresh relay token, a fresh key, the same
    /// channels. Anything in flight on the old connection is lost. Older or equal epochs are ignored, so a late or
    /// repeated notice cannot send the session back to an old key.
    /// </summary>
    public async Task ReattachAsync(int epoch, CancellationToken ct)
    {
        if (epoch <= Epoch) return;
        var token = (await _signaling.RequestRelayTokenAsync(SessionId, ct)).SessionToken;

        // Let go of the old connection first: it is useless now, and the relay hands each role out once.
        var old = Interlocked.Exchange(ref _transport, null);
        if (old is not null) { try { await old.DisposeAsync(); } catch (Exception) { } }

        await ConnectAsync(epoch, token, ct);
    }

    private async Task ConnectAsync(int epoch, string relayToken, CancellationToken ct)
    {
        var crypto = new AesGcmSessionCrypto();
        await crypto.DeriveSessionKeyAsync(SessionId, _myKeys.PrivateKey, _peerKey, SessionKeyContext.ForEpoch(epoch), ct);

        var socket = await ConnectRelayAsync(_options, SessionId, _me, relayToken, ct);
        var transport = new SecureTransport(new RelayTransport(socket, SessionId), crypto, _initiator);
        try
        {
            var opened = new Dictionary<ChannelType, IChannel>();
            foreach (var type in Enum.GetValues<ChannelType>())
                opened[type] = await transport.OpenChannelAsync(type, ct);

            if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(SessionEndpoint));
            var old = Interlocked.Exchange(ref _transport, transport);
            Volatile.Write(ref _epoch, epoch);
            foreach (var (type, channel) in opened) await _channels[type].AttachAsync(channel);
            if (old is not null) { try { await old.DisposeAsync(); } catch (Exception) { } }
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private static async Task<WebSocket> ConnectRelayAsync(
        ClientOptions options, SessionId sessionId, DeviceId me, string token, CancellationToken ct)
    {
        var uri = options.RelayUri(sessionId, me);
        if (options.RelayConnector is not null) return await options.RelayConnector(uri, token, ct);

        var ws = new ClientWebSocket();
        try
        {
            ws.Options.SetRequestHeader("X-Relay-Token", token); // never in the URL: URLs get logged
            await ws.ConnectAsync(uri, ct);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposed, 1);
        foreach (var c in _channels.Values) await c.DisposeAsync();
        var transport = Interlocked.Exchange(ref _transport, null);
        if (transport is not null) { try { await transport.DisposeAsync(); } catch (Exception) { } }
    }
}
