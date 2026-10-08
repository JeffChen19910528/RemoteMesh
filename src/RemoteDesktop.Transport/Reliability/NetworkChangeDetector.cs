using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDesktop.Transport.Reliability;

public enum NetworkChangeKind
{
    None,
    /// <summary>Same interfaces, different IP (DHCP renewal, NAT rebinding on the local side).</summary>
    AddressChanged,
    /// <summary>Interface set changed (Wi-Fi ↔ Ethernet, VPN up/down).</summary>
    InterfaceChanged,
    Lost,
    Restored
}

public sealed record NetworkEndpoint(string InterfaceId, string Address);

public sealed class NetworkSnapshot(IEnumerable<NetworkEndpoint> endpoints)
{
    public IReadOnlySet<NetworkEndpoint> Endpoints { get; } = endpoints.ToHashSet();
    public bool IsEmpty => Endpoints.Count == 0;

    public static NetworkChangeKind Compare(NetworkSnapshot previous, NetworkSnapshot current)
    {
        if (previous.IsEmpty && current.IsEmpty) return NetworkChangeKind.None;
        if (current.IsEmpty) return NetworkChangeKind.Lost;
        if (previous.IsEmpty) return NetworkChangeKind.Restored;

        var prevIfaces = previous.Endpoints.Select(e => e.InterfaceId).ToHashSet();
        var currIfaces = current.Endpoints.Select(e => e.InterfaceId).ToHashSet();
        if (!prevIfaces.SetEquals(currIfaces)) return NetworkChangeKind.InterfaceChanged;
        return previous.Endpoints.SetEquals(current.Endpoints) ? NetworkChangeKind.None : NetworkChangeKind.AddressChanged;
    }

    /// <summary>Operational, non-loopback, non-tunnel interfaces with a routable (non link-local) unicast address.</summary>
    public static NetworkSnapshot Capture()
    {
        var endpoints = new List<NetworkEndpoint>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) continue;
                    if (ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString().StartsWith("169.254.")) continue;
                    endpoints.Add(new NetworkEndpoint(nic.Id, ip.ToString()));
                }
            }
        }
        catch (NetworkInformationException) { /* treat as no information; next poll retries */ }
        return new NetworkSnapshot(endpoints);
    }
}

/// <summary>
/// Detects local network changes by comparing snapshots. OS change notifications only trigger an immediate
/// poll (they are noisy and can arrive in bursts); the comparison decides whether anything actually changed.
/// </summary>
public sealed class NetworkChangeDetector
{
    private readonly Func<NetworkSnapshot> _capture;
    private readonly TimeSpan _pollInterval;
    private readonly object _lock = new();
    private NetworkSnapshot _last;

    public NetworkChangeDetector(Func<NetworkSnapshot>? capture = null, TimeSpan? pollInterval = null)
    {
        _capture = capture ?? NetworkSnapshot.Capture;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _last = _capture();
    }

    public event Action<NetworkChangeKind>? NetworkChanged;

    public NetworkChangeKind Poll()
    {
        var current = _capture();
        NetworkChangeKind kind;
        lock (_lock)
        {
            kind = NetworkSnapshot.Compare(_last, current);
            if (kind != NetworkChangeKind.None) _last = current;
        }
        if (kind != NetworkChangeKind.None) NetworkChanged?.Invoke(kind);
        return kind;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Poll();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Polls immediately whenever the OS reports an address/availability change.</summary>
    public IDisposable AttachToSystemEvents()
    {
        void Handler(object? s, EventArgs e) => Poll();
        void AvailabilityHandler(object? s, NetworkAvailabilityEventArgs e) => Poll();
        NetworkChange.NetworkAddressChanged += Handler;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityHandler;
        return new Detach(() =>
        {
            NetworkChange.NetworkAddressChanged -= Handler;
            NetworkChange.NetworkAvailabilityChanged -= AvailabilityHandler;
        });
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
