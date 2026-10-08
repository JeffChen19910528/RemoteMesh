namespace RemoteDesktop.Core;

public sealed class ConnectionDiagnostics
{
    public ConnectionMode Mode { get; set; }
    public double LatencyMs { get; set; }
    public double PacketLossPercent { get; set; }
    public double BandwidthMbps { get; set; }
    public int Fps { get; set; }
    public string Resolution { get; set; } = string.Empty;
    public string Codec { get; set; } = string.Empty;
    public DateTimeOffset MeasuredAt { get; set; } = DateTimeOffset.UtcNow;
}
