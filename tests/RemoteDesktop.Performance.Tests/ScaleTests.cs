using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesktop.Core;
using RemoteDesktop.Encoding;
using RemoteDesktop.FileTransfer;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;
using RemoteDesktop.Server.Core;
using Xunit.Abstractions;

namespace RemoteDesktop.Performance.Tests;

[Collection("perf")]
[Trait("Category", "Performance")]
public sealed class ScaleTests(ITestOutputHelper output)
{
    // ── Large file transfer ──────────────────────────────────────────────

    private static SessionInfo Connected() => new()
    {
        SessionId = SessionId.New(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        State = SessionState.Connected,
        Permissions = PermissionSet.FullControl()
    };

    private static async Task WriteRandomFileAsync(string path, long bytes)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        var block = new byte[1 << 20];
        var rng = new Random(42);
        for (long written = 0; written < bytes; written += block.Length)
        {
            rng.NextBytes(block);
            await fs.WriteAsync(block.AsMemory(0, (int)Math.Min(block.Length, bytes - written)));
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs));
    }

    [Fact]
    public async Task LargeFile_UploadAndDownload_ThroughEncryptedChannel_StaysStreaming()
    {
        var mb = int.TryParse(Environment.GetEnvironmentVariable("RM_PERF_FILE_MB"), out var v) ? v : 256;
        var dir = Path.Combine(Path.GetTempPath(), "rm-perf-" + Guid.NewGuid().ToString("N"));
        var hostRoot = Path.Combine(dir, "host");
        Directory.CreateDirectory(hostRoot);
        var src = Path.Combine(dir, "big.bin");
        var back = Path.Combine(dir, "back.bin");

        try
        {
            await WriteRandomFileAsync(src, mb * 1048576L);
            var expected = await HashAsync(src);

            await using var link = await Rig.SecureLinkAsync(ChannelType.File);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var host = new FileTransferHost(link.B, Connected(), hostRoot,
                new FileTransferHostOptions { MaxFileSize = 4L << 30, MaxBytesPerSecond = 0 });
            var client = new FileTransferClient(link.A);
            var hostLoop = host.RunAsync(cts.Token);
            var clientLoop = client.RunAsync(cts.Token);

            GC.Collect();
            using (var up = new ResourceScope())
            {
                var outcome = await client.UploadAsync(src, "big.bin", ct: cts.Token);
                Assert.True(outcome.Ok, outcome.Result.ToString());
                Perf.Report(output, "file",
                    $"upload {mb} MB: {up.WallSeconds:F1}s = {mb / up.WallSeconds:F0} MB/s | CPU {up.CpuPercentOfOneCore:F0}% of one core | " +
                    $"peak working set +{up.PeakWorkingSetGrowthMb:F0} MB | alloc {up.AllocatedBytes / 1048576.0:F0} MB");
                Assert.True(up.PeakWorkingSetGrowthMb < 300, $"working set grew {up.PeakWorkingSetGrowthMb:F0} MB (file is {mb} MB)");
                Assert.True(mb / up.WallSeconds > 15, $"{mb / up.WallSeconds:F0} MB/s");
            }
            Assert.Equal(expected, await HashAsync(Path.Combine(hostRoot, "big.bin")));

            using (var down = new ResourceScope())
            {
                var outcome = await client.DownloadAsync("big.bin", back, ct: cts.Token);
                Assert.True(outcome.Ok, outcome.Result.ToString());
                Perf.Report(output, "file",
                    $"download {mb} MB: {down.WallSeconds:F1}s = {mb / down.WallSeconds:F0} MB/s | CPU {down.CpuPercentOfOneCore:F0}% of one core | " +
                    $"peak working set +{down.PeakWorkingSetGrowthMb:F0} MB | alloc {down.AllocatedBytes / 1048576.0:F0} MB");
                Assert.True(down.PeakWorkingSetGrowthMb < 300, $"working set grew {down.PeakWorkingSetGrowthMb:F0} MB");
                Assert.True(mb / down.WallSeconds > 15, $"{mb / down.WallSeconds:F0} MB/s");
            }
            Assert.Equal(expected, await HashAsync(back));

            cts.Cancel();
            await Task.WhenAll(hostLoop, clientLoop);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── Multiple concurrent sessions ─────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    public async Task MultipleSessions_StreamConcurrently_AtTargetFps(int sessions)
    {
        const int fps = 15;
        using var scope = new ResourceScope();

        var runs = await Task.WhenAll(Enumerable.Range(0, sessions).Select(_ =>
            Task.Run(() => Rig.StreamAsync(Scenes.Interactive, fps, VideoQuality.VeryPoor, TimeSpan.FromSeconds(4)))));

        var rendered = runs.Sum(r => r.Rendered);
        var captured = runs.Sum(r => r.Captured);
        var lat = new Samples();
        foreach (var r in runs) lat.AddMs(r.EndToEndMs.P(.95));
        var worstFps = runs.Min(r => r.Rendered) / scope.WallSeconds;
        var gcs = GC.CollectionCount(2);

        Perf.Report(output, "sessions",
            $"{sessions} sessions × 540p@{fps}: rendered {rendered}/{captured} | worst session {worstFps:F1} fps | " +
            $"worst per-session e2e p95 {Samples.Fmt(lat.Max)} | CPU {scope.CpuPercentOfOneCore:F0}% of one core " +
            $"({scope.CpuPercentOfOneCore / sessions:F0}% per session) | peak WS +{scope.PeakWorkingSetGrowthMb:F0} MB " +
            $"({scope.PeakWorkingSetGrowthMb / sessions:F0} MB/session) | gen2 GCs so far {gcs}");

        Assert.True(rendered >= captured * 0.95, $"dropped {captured - rendered} frames");
        Assert.True(worstFps >= fps * 0.75, $"slowest session {worstFps:F1} fps");
    }

    // ── Server: connection establishment, registration load, relay ───────

    private sealed class ServerEnv : IAsyncDisposable
    {
        private readonly List<HubConnection> _connections = new();
        public WebApplicationFactory<Program> Factory { get; } = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton(new RemoteDesktop.Server.Relay.RelayOptions
            {
                // The load test opens many connections from one in-process address.
                ConnectionsPerMinutePerAddress = 100_000
            })));

        public async Task<(HubConnection Conn, DeviceInfoDto Dto, DeviceKeyPair Keys)> NewDeviceAsync(string name, bool register = true)
        {
            var conn = new HubConnectionBuilder()
                .WithUrl("http://localhost/hubs/signaling", o =>
                {
                    o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                })
                .Build();
            lock (_connections) _connections.Add(conn);
            var keys = DeviceKeyGenerator.Generate();
            var dto = new DeviceInfoDto
            {
                DeviceId = DeviceId.New().ToString(), DeviceName = name, Platform = "Windows",
                PublicKeyBase64 = Convert.ToBase64String(keys.PublicKey)
            };
            await conn.StartAsync();
            if (register) await RegisterAsync(conn, dto, keys);
            return (conn, dto, keys);
        }

        public static async Task RegisterAsync(HubConnection c, DeviceInfoDto dto, DeviceKeyPair keys)
        {
            var challenge = Convert.FromBase64String(await c.InvokeAsync<string>("BeginRegistration"));
            var sig = Convert.ToBase64String(DeviceKeyGenerator.Sign(keys,
                RegistrationProof.BuildPayload(challenge, dto.DeviceId, c.ConnectionId!)));
            await c.InvokeAsync("RegisterDevice", dto, sig);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in _connections) await c.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectionEstablishment_RegisterRequestApproveConnect()
    {
        await using var env = new ServerEnv();
        var total = new Samples();
        var handshake = new Samples();

        for (var i = 0; i < 40; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            var alice = await env.NewDeviceAsync("A");
            var bob = await env.NewDeviceAsync("B");
            var t1 = Stopwatch.GetTimestamp();

            var incoming = new TaskCompletionSource<SessionDto>();
            bob.Conn.On<SessionDto>("IncomingSessionRequest", d => incoming.TrySetResult(d));
            var session = await alice.Conn.InvokeAsync<SessionDto>("RequestSession", bob.Dto.DeviceId);
            await incoming.Task;
            await bob.Conn.InvokeAsync("RespondToSession", session.SessionId, true, (long)Permission.ViewOnly);
            await alice.Conn.InvokeAsync("ReportConnectionMode", session.SessionId, "Direct");
            var t2 = Stopwatch.GetTimestamp();

            if (i >= 5) // first iterations include JIT
            {
                handshake.AddTicks(t1 - t0);
                total.AddTicks(t2 - t0);
            }
        }

        Perf.Report(output, "connect",
            $"two devices connect+authenticate (challenge-response): {handshake} | request→approve→connected: " +
            $"{total} total (in-process server, no network latency)");
        Assert.True(total.P(.95) < 1000, $"p95 {total.P(.95):F0}ms");
    }

    [Fact]
    public async Task Server_ManyConcurrentDevices_RegisterAndStayResponsive()
    {
        const int devices = 200;
        await using var env = new ServerEnv();
        using var scope = new ResourceScope();

        var all = await Task.WhenAll(Enumerable.Range(0, devices).Select(i => env.NewDeviceAsync("D" + i)));
        var registerSeconds = scope.WallSeconds;

        var heartbeat = new Samples();
        await Task.WhenAll(all.Select(async d =>
        {
            for (var i = 0; i < 5; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                await d.Conn.InvokeAsync("Heartbeat");
                lock (heartbeat) heartbeat.AddTicks(Stopwatch.GetTimestamp() - t0);
            }
        }));

        Perf.Report(output, "server",
            $"{devices} devices registered concurrently in {registerSeconds:F1}s ({devices / registerSeconds:F0}/s) | " +
            $"heartbeat under load {heartbeat} | server+clients CPU {scope.CpuPercentOfOneCore:F0}% of one core | peak WS +{scope.PeakWorkingSetGrowthMb:F0} MB");
        Assert.True(heartbeat.P(.95) < 500, $"heartbeat p95 {heartbeat.P(.95):F0}ms");
    }

    [Fact]
    public async Task Relay_ManyPairs_ForwardWithLowLatency()
    {
        const int pairs = 50;
        const int messages = 100;
        await using var env = new ServerEnv();
        var sessions = env.Factory.Services.GetRequiredService<ISessionManager>();
        var tokens = env.Factory.Services.GetRequiredService<ISessionTokenService>();

        async Task<WebSocket> Connect(SessionId s, DeviceId d, string token)
        {
            var c = env.Factory.Server.CreateWebSocketClient();
            c.ConfigureRequest = r => r.Headers["X-Relay-Token"] = token;
            return await c.ConnectAsync(new Uri($"ws://localhost/ws/relay?sessionId={s}&deviceId={d}"), CancellationToken.None);
        }

        var lat = new Samples();
        using var scope = new ResourceScope();
        await Task.WhenAll(Enumerable.Range(0, pairs).Select(async _ =>
        {
            var (r, t) = (DeviceId.New(), DeviceId.New());
            var s = await sessions.CreateAsync(r, t);
            await sessions.UpdateStateAsync(s.SessionId, SessionState.EstablishingTransport);
            var wsA = await Connect(s.SessionId, r, tokens.Issue(s.SessionId, r));
            var wsB = await Connect(s.SessionId, t, tokens.Issue(s.SessionId, t));
            var buf = new byte[2048];
            var payload = new byte[1024];

            var echo = Task.Run(async () =>
            {
                for (var i = 0; i < messages; i++)
                {
                    var rr = await wsB.ReceiveAsync(buf, CancellationToken.None);
                    await wsB.SendAsync(buf.AsMemory(0, rr.Count), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            });
            for (var i = 0; i < messages; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                await wsA.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
                await wsA.ReceiveAsync(buf, CancellationToken.None);
                lock (lat) lat.AddTicks(Stopwatch.GetTimestamp() - t0);
            }
            await echo;
        }));

        var total = pairs * messages * 2;
        Perf.Report(output, "relay",
            $"{pairs} concurrent relay pairs × {messages} round trips (1 KiB): RTT {lat} | {total / scope.WallSeconds:F0} forwarded msgs/s | " +
            $"CPU {scope.CpuPercentOfOneCore:F0}% of one core");
        Assert.True(lat.P(.99) < 250, $"relay RTT p99 {lat.P(.99):F0}ms");
    }
}
