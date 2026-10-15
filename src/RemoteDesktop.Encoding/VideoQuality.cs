namespace RemoteDesktop.Encoding;

public enum QualityPreset { Excellent, Good, Poor, VeryPoor }

public sealed class VideoQuality
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; }
    public int BitrateKbps { get; init; }
    public int KeyframeIntervalSeconds { get; init; } = 2;

    public static readonly VideoQuality Excellent = new() { Width = 1920, Height = 1080, Fps = 60, BitrateKbps = 10000 };
    public static readonly VideoQuality Good       = new() { Width = 1600, Height = 900,  Fps = 30, BitrateKbps = 5000 };
    public static readonly VideoQuality Poor       = new() { Width = 1280, Height = 720,  Fps = 20, BitrateKbps = 2000 };
    public static readonly VideoQuality VeryPoor   = new() { Width = 960,  Height = 540,  Fps = 15, BitrateKbps = 800 };
}
