using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace RemoteDesktop.Performance.Tests;

[CollectionDefinition("perf", DisableParallelization = true)]
public sealed class PerfCollection { }

/// <summary>Latency samples in milliseconds.</summary>
public sealed class Samples
{
    private readonly List<double> _ms = new();
    public int Count => _ms.Count;

    public void AddTicks(long ticks) => _ms.Add(ticks * 1000.0 / Stopwatch.Frequency);
    public void AddMs(double ms) => _ms.Add(ms);

    public double Mean => _ms.Count == 0 ? 0 : _ms.Average();
    public double Min => _ms.Count == 0 ? 0 : _ms.Min();
    public double Max => _ms.Count == 0 ? 0 : _ms.Max();
    public double P(double q)
    {
        if (_ms.Count == 0) return 0;
        var sorted = _ms.OrderBy(x => x).ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1 < 0 ? 0 : (int)Math.Ceiling(q * sorted.Count) - 1)];
    }

    public static string Fmt(double ms) => ms < 1 ? $"{ms * 1000:F0}µs" : $"{ms:F2}ms";

    public override string ToString() =>
        $"n={Count} mean={Fmt(Mean)} p50={Fmt(P(.5))} p95={Fmt(P(.95))} p99={Fmt(P(.99))} max={Fmt(Max)}";
}

/// <summary>
/// Measures wall time, process CPU, allocations and peak working set over a block of work.
/// Working set is sampled every 20 ms because Process.PeakWorkingSet64 spans the whole test run.
/// </summary>
public sealed class ResourceScope : IDisposable
{
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly TimeSpan _cpu0;
    private readonly long _alloc0;
    private readonly long _ws0;
    private readonly Timer _sampler;
    private long _peakWs;

    public ResourceScope()
    {
        var p = Process.GetCurrentProcess();
        _cpu0 = p.TotalProcessorTime;
        _alloc0 = GC.GetTotalAllocatedBytes(precise: false);
        _ws0 = p.WorkingSet64;
        _peakWs = _ws0;
        _sampler = new Timer(_ =>
        {
            var ws = Process.GetCurrentProcess().WorkingSet64;
            long cur;
            while (ws > (cur = Interlocked.Read(ref _peakWs)) && Interlocked.CompareExchange(ref _peakWs, ws, cur) != cur) { }
        }, null, 0, 20);
    }

    public double WallSeconds => _wall.Elapsed.TotalSeconds;
    public double CpuSeconds => (Process.GetCurrentProcess().TotalProcessorTime - _cpu0).TotalSeconds;
    /// <summary>100 = one fully busy core.</summary>
    public double CpuPercentOfOneCore => CpuSeconds / Math.Max(WallSeconds, 1e-9) * 100;
    public long AllocatedBytes => GC.GetTotalAllocatedBytes(precise: false) - _alloc0;
    public double PeakWorkingSetGrowthMb => (Interlocked.Read(ref _peakWs) - _ws0) / 1048576.0;

    public void Dispose() => _sampler.Dispose();
}

public static class Perf
{
    public static string Machine =>
        $"{RuntimeInformation.OSDescription.Trim()} | {Environment.ProcessorCount} logical cores | {RuntimeInformation.FrameworkDescription} | " +
        $"{(AppContext.TryGetSwitch("System.GC.Server", out var s) && s ? "server" : "workstation")} GC";

    /// <summary>Write to test output and (when RM_PERF_OUT is set) to a results file used for docs/PERFORMANCE.md.</summary>
    public static void Report(ITestOutputHelper output, string area, string line)
    {
        output.WriteLine($"[{area}] {line}");
        var path = Environment.GetEnvironmentVariable("RM_PERF_OUT");
        if (!string.IsNullOrEmpty(path))
            lock (typeof(Perf)) File.AppendAllText(path, $"{area}\t{line}{Environment.NewLine}");
    }

    public static string Mb(long bytes) => $"{bytes / 1048576.0:F1} MB";
}
