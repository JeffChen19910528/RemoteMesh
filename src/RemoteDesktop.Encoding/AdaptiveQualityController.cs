namespace RemoteDesktop.Encoding;

public readonly record struct NetworkStats(double RttMs, double LossPercent, double BandwidthKbps);

/// <summary>
/// Picks a quality tier from network stats. Degrades immediately; upgrades one tier at a time
/// only after several consecutive good samples, to avoid oscillation.
/// </summary>
public sealed class AdaptiveQualityController
{
    private readonly int _upgradeAfter;
    private int _goodStreak;

    public AdaptiveQualityController(QualityPreset initial = QualityPreset.Good, int upgradeAfterSamples = 5)
    {
        Preset = initial;
        _upgradeAfter = upgradeAfterSamples;
    }

    public QualityPreset Preset { get; private set; }
    public VideoQuality Quality => ToQuality(Preset);

    public static VideoQuality ToQuality(QualityPreset p) => p switch
    {
        QualityPreset.Excellent => VideoQuality.Excellent,
        QualityPreset.Good => VideoQuality.Good,
        QualityPreset.Poor => VideoQuality.Poor,
        _ => VideoQuality.VeryPoor
    };

    public static QualityPreset Recommend(NetworkStats s)
    {
        if (s.LossPercent > 10 || s.RttMs > 400 || s.BandwidthKbps < 1000) return QualityPreset.VeryPoor;
        if (s.LossPercent > 5 || s.RttMs > 200 || s.BandwidthKbps < 3000) return QualityPreset.Poor;
        if (s.LossPercent > 1 || s.RttMs > 100 || s.BandwidthKbps < 8000) return QualityPreset.Good;
        return QualityPreset.Excellent;
    }

    /// <summary>Feeds a sample; returns true when the preset changed.</summary>
    public bool Update(NetworkStats stats)
    {
        var target = Recommend(stats);
        if (target > Preset) // worse network (enum order: Excellent < ... < VeryPoor)
        {
            Preset = target;
            _goodStreak = 0;
            return true;
        }
        if (target < Preset)
        {
            if (++_goodStreak >= _upgradeAfter)
            {
                Preset--;
                _goodStreak = 0;
                return true;
            }
            return false;
        }
        _goodStreak = 0;
        return false;
    }
}
