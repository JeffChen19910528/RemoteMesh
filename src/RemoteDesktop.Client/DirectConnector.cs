using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteDesktop.Core;
using RemoteDesktop.Signaling;

namespace RemoteDesktop.Client;

/// <summary>
/// Transport negotiation messages travelling over the signaling connection, buffered per session so one that arrives
/// before the receiving side starts waiting for it is not lost.
/// </summary>
internal sealed class TransportSignals
{
    private const int MaxPerSession = 32;
    private const int MaxSessions = 64;

    private readonly ISignalingClient _signaling;
    private readonly ConcurrentDictionary<SessionId, Inbox> _inboxes = new();

    public TransportSignals(ISignalingClient signaling)
    {
        _signaling = signaling;
        signaling.TransportSignalReceived += OnReceived;
    }

    private sealed class Inbox
    {
        public readonly object Lock = new();
        public readonly List<DirectSignal> Items = [];
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void OnReceived(SessionId session, string payload)
    {
        var signal = DirectSignal.TryParse(payload);
        if (signal is null) return;
        if (!_inboxes.ContainsKey(session) && _inboxes.Count >= MaxSessions) return;
        var inbox = _inboxes.GetOrAdd(session, _ => new Inbox());
        lock (inbox.Lock)
        {
            if (inbox.Items.Count >= MaxPerSession) inbox.Items.RemoveAt(0);
            inbox.Items.Add(signal);
            var changed = inbox.Changed;
            inbox.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    public Task SendAsync(SessionId session, DirectSignal signal, CancellationToken ct) =>
        _signaling.SendTransportSignalAsync(session, signal.ToJson(), ct);

    /// <summary>Waits for a message of <paramref name="kind"/> for <paramref name="epoch"/>; null on timeout.</summary>
    public async Task<DirectSignal?> WaitAsync(SessionId session, int epoch, string kind, TimeSpan timeout, CancellationToken ct)
    {
        var inbox = _inboxes.GetOrAdd(session, _ => new Inbox());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (inbox.Lock)
            {
                var i = inbox.Items.FindIndex(s => s.Epoch == epoch && s.Kind == kind);
                if (i >= 0)
                {
                    var found = inbox.Items[i];
                    inbox.Items.RemoveAt(i);
                    return found;
                }
                // Older epochs can never be used again.
                inbox.Items.RemoveAll(s => s.Epoch < epoch);
                changed = inbox.Changed.Task;
            }
            try { await changed.WaitAsync(limit.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }
    }

    public void Forget(SessionId session) => _inboxes.TryRemove(session, out _);

    public void Detach() => _signaling.TransportSignalReceived -= OnReceived;
}

internal sealed record DirectSignal(string Kind, int Epoch, string[] Addresses)
{
    public const string Offer = "offer";
    public const string UseRelay = "relay";

    public string ToJson() => JsonSerializer.Serialize(new Wire { K = Kind, E = Epoch, A = Addresses });

    public static DirectSignal? TryParse(string json)
    {
        try
        {
            var w = JsonSerializer.Deserialize<Wire>(json);
            if (w?.K is not (Offer or UseRelay)) return null;
            return new DirectSignal(w.K, w.E, (w.A ?? []).Take(16).ToArray());
        }
        catch (JsonException) { return null; }
    }

    private sealed class Wire
    {
        [JsonPropertyName("k")] public string? K { get; set; }
        [JsonPropertyName("e")] public int E { get; set; }
        [JsonPropertyName("a")] public string[]? A { get; set; }
    }
}

/// <summary>
/// Tries to reach the other computer without the relay. The controlled side listens on a TCP port and offers its
/// addresses over signaling; the side that asked connects to them. If nothing works the asking side says so and both
/// use the relay, so a failed attempt costs a couple of seconds and nothing else. Whatever connects here is only a
/// byte pipe: the caller still runs the TLS handshake that proves the pinned device keys, so a stranger who reaches
/// the port gets nothing.
/// </summary>
internal sealed class DirectConnector
{
    private static readonly TimeSpan OfferWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SlowSignalingGrace = TimeSpan.FromSeconds(4);
    private const int MaxIncomingAttempts = 4;

    private readonly ClientOptions _options;
    private readonly TransportSignals _signals;
    private readonly SessionId _session;
    private readonly bool _initiator;

    public DirectConnector(ClientOptions options, TransportSignals signals, SessionId session, bool initiator)
    {
        _options = options;
        _signals = signals;
        _session = session;
        _initiator = initiator;
    }

    /// <summary>
    /// Returns a connected, authenticated socket, or null when the relay should be used. <paramref name="authenticate"/>
    /// runs the TLS handshake over a candidate socket and throws if the peer is not the pinned one.
    /// </summary>
    public async Task<WebSocket?> TryAsync(int epoch, Func<WebSocket, Task<WebSocket>> authenticate, CancellationToken ct)
    {
        try
        {
            return _initiator
                ? await ConnectAsync(epoch, authenticate, ct)
                : await ListenAsync(epoch, authenticate, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; } // direct is an optimisation; never fatal
    }

    private async Task<WebSocket?> ConnectAsync(int epoch, Func<WebSocket, Task<WebSocket>> authenticate, CancellationToken ct)
    {
        var offer = await _signals.WaitAsync(_session, epoch, DirectSignal.Offer, OfferWait, ct);
        if (offer is not null)
        {
            var tcp = await ConnectAnyAsync(offer.Addresses, _options.DirectConnectTimeout, ct);
            if (tcp is not null)
            {
                try
                {
                    var raw = WebSocket.CreateFromStream(tcp.GetStream(), new WebSocketCreationOptions { IsServer = false });
                    return await authenticate(raw);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { tcp.Dispose(); }
            }
        }
        await TrySignalAsync(new DirectSignal(DirectSignal.UseRelay, epoch, []), ct);
        return null;
    }

    private async Task<WebSocket?> ListenAsync(int epoch, Func<WebSocket, Task<WebSocket>> authenticate, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.IPv6Any, _options.DirectPort);
        listener.Server.DualMode = true;
        try { listener.Start(1); }
        catch (SocketException)
        {
            await TrySignalAsync(new DirectSignal(DirectSignal.Offer, epoch, []), ct);
            return null;
        }
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var addresses = LocalAddresses(port).Concat(_options.DirectAdvertisedAddresses).Distinct().Take(16).ToArray();
            await TrySignalAsync(new DirectSignal(DirectSignal.Offer, epoch, addresses), ct);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(OfferWait + _options.DirectConnectTimeout + SlowSignalingGrace);

            var relay = _signals.WaitAsync(_session, epoch, DirectSignal.UseRelay, Timeout.InfiniteTimeSpan, limit.Token);
            for (var attempt = 0; attempt < MaxIncomingAttempts; attempt++)
            {
                var accept = listener.AcceptTcpClientAsync(limit.Token).AsTask();
                var first = await Task.WhenAny(accept, relay);
                if (first == relay)
                {
                    limit.Cancel();
                    try { (await accept).Dispose(); } catch (Exception) { }
                    return null;
                }

                var client = await accept;
                try
                {
                    client.NoDelay = true;
                    var raw = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = true });
                    return await authenticate(raw);
                }
                catch (Exception) when (!limit.IsCancellationRequested) { client.Dispose(); } // not the peer: keep waiting
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { listener.Stop(); }
    }

    private async Task TrySignalAsync(DirectSignal signal, CancellationToken ct)
    {
        try { await _signals.SendAsync(_session, signal, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { } // the other side then falls back on its own timeout
    }

    private static async Task<TcpClient?> ConnectAnyAsync(string[] addresses, TimeSpan timeout, CancellationToken ct)
    {
        var endpoints = addresses.Select(ParseEndpoint).Where(e => e is not null).Select(e => e!).ToList();
        if (endpoints.Count == 0) return null;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var attempts = endpoints.Select(e => TryConnectAsync(e, limit.Token)).ToList();
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts);
            attempts.Remove(done);
            var client = await done;
            if (client is null) continue;
            limit.Cancel(); // the others are not needed
            foreach (var rest in attempts)
                _ = rest.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); }, TaskScheduler.Default);
            return client;
        }
        return null;
    }

    private static async Task<TcpClient?> TryConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(endpoint, ct);
            return client;
        }
        catch (Exception)
        {
            client.Dispose();
            return null;
        }
    }

    private static IPEndPoint? ParseEndpoint(string text) =>
        IPEndPoint.TryParse(text, out var ep) && ep.Port is > 0 and <= 65535 && !ep.Address.Equals(IPAddress.Any) ? ep : null;

    private IEnumerable<string> LocalAddresses(int port)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback && !_options.DirectIncludeLoopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                var a = u.Address;
                if (a.IsIPv6LinkLocal || a.IsIPv6Multicast) continue;
                if (IPAddress.IsLoopback(a) && !_options.DirectIncludeLoopback) continue;
                yield return new IPEndPoint(a, port).ToString();
            }
        }
    }
}
