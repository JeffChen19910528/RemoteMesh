using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Persistence;

namespace RemoteDesktop.Server.Persistence.Tests;

/// <summary>
/// Runs only when <c>RM_TEST_PG</c> holds the connection string of a PostgreSQL server that tests may create and drop
/// databases on (for example <c>Host=localhost;Port=54329;Username=postgres;Password=...</c>). Each test gets its own
/// empty database, so nothing leaks between tests and nothing real is touched.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "RM_TEST_PG";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to a PostgreSQL connection string to run the database tests.";
    }
}

public abstract class PostgresTestBase : IAsyncLifetime
{
    private readonly string _admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable) ?? "";
    private readonly string _dbName = "rm_test_" + Guid.NewGuid().ToString("N");

    protected string ConnectionString => new NpgsqlConnectionStringBuilder(_admin) { Database = _dbName, Pooling = false }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (_admin.Length == 0) return;
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin) { Database = "postgres", Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\"", c);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_admin.Length == 0) return;
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin) { Database = "postgres", Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A "server instance": its own connection pool and context factory over the same database.</summary>
    protected async Task<IDbContextFactory<RemoteMeshDbContext>> OpenAsync()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<RemoteMeshDbContext>(o => o.UseNpgsql(ConnectionString));
        var factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<RemoteMeshDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        return factory;
    }

    protected static DeviceInfo Device(string name = "pc") => new()
    {
        DeviceId = DeviceId.New(), DeviceName = name, Platform = "windows", CreatedAt = DateTimeOffset.UtcNow
    };
}

public sealed class DeviceRegistryTests : PostgresTestBase
{
    [PostgresFact]
    public async Task ADeviceAndItsPublicKey_AreFoundAgain_ByAnotherInstance()
    {
        var device = Device("office pc");
        var key = new byte[] { 1, 2, 3, 250, 251, 252 };
        await new PostgresDeviceRegistry(await OpenAsync()).RegisterAsync(device, key);

        var other = new PostgresDeviceRegistry(await OpenAsync()); // "after a restart"
        var found = await other.FindAsync(device.DeviceId);
        Assert.NotNull(found);
        Assert.Equal("office pc", found.DeviceName);
        Assert.Equal("windows", found.Platform);
        Assert.Equal(key, await other.GetPublicKeyAsync(device.DeviceId));
    }

    [PostgresFact]
    public async Task UnknownDevices_AreNotFound()
    {
        var registry = new PostgresDeviceRegistry(await OpenAsync());
        Assert.Null(await registry.FindAsync(DeviceId.New()));
        Assert.Null(await registry.GetPublicKeyAsync(DeviceId.New()));
    }

    [PostgresFact]
    public async Task RegisteringAgain_ReplacesTheEntry_AndParallelRegistrationsDoNotCollide()
    {
        var registry = new PostgresDeviceRegistry(await OpenAsync());
        var device = Device("first");
        await registry.RegisterAsync(device, [1]);
        await registry.RegisterAsync(new DeviceInfo { DeviceId = device.DeviceId, DeviceName = "second", Platform = "windows", CreatedAt = device.CreatedAt }, [2]);
        Assert.Equal("second", (await registry.FindAsync(device.DeviceId))!.DeviceName);
        Assert.Equal(new byte[] { 2 }, await registry.GetPublicKeyAsync(device.DeviceId));

        var same = Device("racing");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => registry.RegisterAsync(same, [(byte)i])));
        Assert.NotNull(await registry.FindAsync(same.DeviceId));
    }

    [PostgresFact]
    public async Task AnUnregisteredDevice_IsGone()
    {
        var registry = new PostgresDeviceRegistry(await OpenAsync());
        var device = Device();
        await registry.RegisterAsync(device, [9]);
        await registry.UnregisterAsync(device.DeviceId);
        Assert.Null(await registry.FindAsync(device.DeviceId));
        Assert.Null(await registry.GetPublicKeyAsync(device.DeviceId));
    }
}

public sealed class ShortIdDirectoryTests : PostgresTestBase
{
    [PostgresFact]
    public async Task ADevicesNumber_SurvivesARestart_AndResolvesBack()
    {
        var device = DeviceId.New();
        var number = await new PostgresShortIdDirectory(await OpenAsync()).AssignAsync(device, null);
        Assert.Matches("^[1-9][0-9]{8}$", number);

        var afterRestart = new PostgresShortIdDirectory(await OpenAsync());
        Assert.Equal(number, await afterRestart.GetAsync(device));
        Assert.Equal(device, await afterRestart.ResolveAsync(number));
        Assert.Equal(number, await afterRestart.AssignAsync(device, "999999999")); // asking again changes nothing
    }

    [PostgresFact]
    public async Task ParallelAssignments_NeverHandOutTheSameNumberTwice()
    {
        var dir = new PostgresShortIdDirectory(await OpenAsync());
        var devices = Enumerable.Range(0, 300).Select(_ => DeviceId.New()).ToArray();
        var numbers = new string[devices.Length];

        await Parallel.ForAsync(0, devices.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) => numbers[i] = await dir.AssignAsync(devices[i], null));

        Assert.Equal(devices.Length, numbers.Distinct().Count());
        for (var i = 0; i < devices.Length; i++) Assert.Equal(devices[i], await dir.ResolveAsync(numbers[i]));
    }

    [PostgresFact]
    public async Task ManyDevicesAskingForTheSameNumber_OnlyOneGetsIt_EvenFromDifferentInstances()
    {
        var a = new PostgresShortIdDirectory(await OpenAsync());
        var b = new PostgresShortIdDirectory(await OpenAsync()); // two server instances on one database
        var devices = Enumerable.Range(0, 60).Select(_ => DeviceId.New()).ToArray();
        var numbers = new string[devices.Length];

        await Parallel.ForAsync(0, devices.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) => numbers[i] = await (i % 2 == 0 ? a : b).AssignAsync(devices[i], "555000111"));

        Assert.Single(numbers, "555000111");
        Assert.Equal(devices.Length, numbers.Distinct().Count());
    }

    [PostgresFact]
    public async Task OneDeviceAskingManyTimesAtOnce_EndsUpWithExactlyOneNumber()
    {
        var dir = new PostgresShortIdDirectory(await OpenAsync());
        var device = DeviceId.New();
        var numbers = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => dir.AssignAsync(device, null)));
        Assert.Single(numbers.Distinct());
        Assert.Equal(numbers[0], await dir.GetAsync(device));
    }

    [PostgresFact]
    public async Task UnknownNumbers_AndNonsense_ResolveToNothing()
    {
        var dir = new PostgresShortIdDirectory(await OpenAsync());
        Assert.Null(await dir.ResolveAsync("123456789"));
        Assert.Null(await dir.ResolveAsync("'; DROP TABLE \"ShortIds\"; --"));
        Assert.Null(await dir.GetAsync(DeviceId.New()));
        var d = DeviceId.New();
        var number = await dir.AssignAsync(d, "not a number'; --"); // junk preference: a random number, no error
        Assert.Matches("^[1-9][0-9]{8}$", number);
    }
}

public sealed class TrustedDeviceStoreTests : PostgresTestBase
{
    [PostgresFact]
    public async Task TrustLastsAcrossARestart_WithItsPermissions_AndCanBeRemoved()
    {
        var owner = DeviceId.New();
        var friend = DeviceId.New();
        await new PostgresTrustedDeviceStore(await OpenAsync()).AddAsync(new TrustedDevice
        {
            OwnerDeviceId = owner, TrustedDeviceId = friend, TrustedDeviceName = "laptop",
            DefaultPermissions = Permission.ViewScreen | Permission.ControlMouse
        });

        var store = new PostgresTrustedDeviceStore(await OpenAsync());
        Assert.True(await store.IsTrustedAsync(owner, friend));
        Assert.False(await store.IsTrustedAsync(friend, owner)); // trust is one-way
        var entry = await store.GetAsync(owner, friend);
        Assert.Equal("laptop", entry!.TrustedDeviceName);
        Assert.Equal(Permission.ViewScreen | Permission.ControlMouse, entry.DefaultPermissions);
        Assert.Single(await store.ListAsync(owner));
        Assert.Empty(await store.ListAsync(friend));

        await store.RemoveAsync(owner, friend);
        Assert.False(await store.IsTrustedAsync(owner, friend));
        Assert.Null(await store.GetAsync(owner, friend));
    }

    [PostgresFact]
    public async Task TrustingAgain_UpdatesThePermissions_InsteadOfFailing()
    {
        var store = new PostgresTrustedDeviceStore(await OpenAsync());
        var owner = DeviceId.New();
        var friend = DeviceId.New();
        await store.AddAsync(new TrustedDevice { OwnerDeviceId = owner, TrustedDeviceId = friend, DefaultPermissions = Permission.ViewScreen });
        await store.AddAsync(new TrustedDevice { OwnerDeviceId = owner, TrustedDeviceId = friend, DefaultPermissions = Permission.ViewScreen | Permission.ControlKeyboard });
        Assert.Equal(Permission.ViewScreen | Permission.ControlKeyboard, (await store.GetAsync(owner, friend))!.DefaultPermissions);
        Assert.Single(await store.ListAsync(owner));
    }
}

public sealed class AuditLogTests : PostgresTestBase
{
    [PostgresFact]
    public async Task EventsAreKept_AndFoundByActorOrTarget_InTimeOrder()
    {
        var log = new PostgresAuditLog(await OpenAsync());
        var a = DeviceId.New();
        var b = DeviceId.New();
        var session = SessionId.New();
        var t0 = DateTimeOffset.UtcNow;
        await log.WriteAsync(new AuditEvent { Timestamp = t0, ActorDeviceId = a, TargetDeviceId = b, SessionId = session, EventType = AuditEventType.RemoteSessionStarted, Success = true, Reason = "trusted-device" });
        await log.WriteAsync(new AuditEvent { Timestamp = t0.AddSeconds(1), ActorDeviceId = b, EventType = AuditEventType.DeviceRegistered, Success = false });
        await log.WriteAsync(new AuditEvent { Timestamp = t0.AddSeconds(2), ActorDeviceId = DeviceId.New(), EventType = AuditEventType.DeviceRegistered, Success = true });

        var after = new PostgresAuditLog(await OpenAsync());
        var forB = await after.QueryAsync(b, t0.AddMinutes(-1), t0.AddMinutes(1));
        Assert.Equal(2, forB.Count);
        Assert.Equal(AuditEventType.RemoteSessionStarted, forB[0].EventType);
        Assert.Equal(session, forB[0].SessionId);
        Assert.Equal(a, forB[0].ActorDeviceId);
        Assert.Equal(b, forB[0].TargetDeviceId);
        Assert.Equal("trusted-device", forB[0].Reason);
        Assert.True(forB[0].Success);
        Assert.False(forB[1].Success);
        Assert.Single(await after.QueryAsync(a, t0.AddMinutes(-1), t0.AddMinutes(1)));
        Assert.Empty(await after.QueryAsync(a, t0.AddMinutes(5), t0.AddMinutes(6)));
    }

    [PostgresFact]
    public async Task APastRetentionEntry_IsPruned_AndARecentOneStays()
    {
        var log = new PostgresAuditLog(await OpenAsync());
        var who = DeviceId.New();
        await log.WriteAsync(new AuditEvent { Timestamp = DateTimeOffset.UtcNow.AddDays(-100), ActorDeviceId = who, EventType = AuditEventType.DeviceRegistered, Success = true });
        await log.WriteAsync(new AuditEvent { Timestamp = DateTimeOffset.UtcNow, ActorDeviceId = who, EventType = AuditEventType.DeviceRegistered, Success = true });

        Assert.Equal(1, await log.PruneAsync(DateTimeOffset.UtcNow.AddDays(-90)));
        Assert.Single(await log.QueryAsync(who, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddDays(1)));
    }

    [PostgresFact]
    public async Task AHugeReason_IsCut_NotRefused()
    {
        var log = new PostgresAuditLog(await OpenAsync());
        var who = DeviceId.New();
        await log.WriteAsync(new AuditEvent { ActorDeviceId = who, EventType = AuditEventType.DeviceRegistered, Success = false, Reason = new string('x', 50_000) });
        var back = await log.QueryAsync(who, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(1024, back.Single().Reason!.Length);
    }
}

public sealed class DatabaseOptionsTests
{
    [Fact]
    public void WithoutAConnectionString_TheDatabaseIsOff()
    {
        var o = new DatabaseOptions();
        o.Validate();
        Assert.False(o.Enabled);
    }

    [Fact]
    public void ABadConnectionString_IsRefused_WithoutRepeatingThePassword()
    {
        var o = new DatabaseOptions { ConnectionString = "Host=db;Password=hunter2;bogus-setting-without-value" };
        var ex = Assert.Throws<InvalidOperationException>(o.Validate);
        Assert.DoesNotContain("hunter2", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    public void ARetentionOutOfRange_IsRefused(int days) =>
        Assert.Throws<InvalidOperationException>(() => new DatabaseOptions { AuditRetentionDays = days }.Validate());

    [Fact]
    public void AValidConnectionString_IsAccepted()
    {
        var o = new DatabaseOptions { ConnectionString = "Host=localhost;Database=remotemesh;Username=app;Password=x" };
        o.Validate();
        Assert.True(o.Enabled);
    }
}

/// <summary>The real server, started twice against one database: what the first one learned, the second one knows.</summary>
public sealed class ServerRestartTests : PostgresTestBase
{
    private WebApplicationFactory<Program> Server() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("RemoteMesh:Database:ConnectionString", ConnectionString);
    });

    [PostgresFact]
    public async Task AStartedServer_UsesPostgres_AndARestartedOneRemembersDevicesAndShortIds()
    {
        var device = Device("remembered");
        string number;
        await using (var first = Server())
        {
            Assert.IsType<PostgresDeviceRegistry>(first.Services.GetRequiredService<IDeviceRegistry>());
            Assert.IsType<PostgresShortIdDirectory>(first.Services.GetRequiredService<IShortIdDirectory>());
            Assert.IsType<PostgresTrustedDeviceStore>(first.Services.GetRequiredService<ITrustedDeviceStore>());
            Assert.IsType<PostgresAuditLog>(first.Services.GetRequiredService<IAuditLog>());
            await first.Services.GetRequiredService<IDeviceRegistry>().RegisterAsync(device, [7, 7, 7]);
            number = await first.Services.GetRequiredService<IShortIdDirectory>().AssignAsync(device.DeviceId, null);
        }

        await using var second = Server(); // the first server is gone
        Assert.Equal("remembered", (await second.Services.GetRequiredService<IDeviceRegistry>().FindAsync(device.DeviceId))!.DeviceName);
        Assert.Equal(new byte[] { 7, 7, 7 }, await second.Services.GetRequiredService<IDeviceRegistry>().GetPublicKeyAsync(device.DeviceId));
        Assert.Equal(device.DeviceId, await second.Services.GetRequiredService<IShortIdDirectory>().ResolveAsync(number));
    }

    [Fact]
    public async Task WithoutADatabase_TheServerStillRunsInMemory()
    {
        await using var server = new WebApplicationFactory<Program>();
        Assert.IsNotType<PostgresDeviceRegistry>(server.Services.GetRequiredService<IDeviceRegistry>());
        Assert.IsNotType<PostgresShortIdDirectory>(server.Services.GetRequiredService<IShortIdDirectory>());
        await Task.CompletedTask;
    }
}

public sealed class MigrationTests : PostgresTestBase
{
    [PostgresFact]
    public async Task OnAnEmptyDatabase_TheMigrationsCreateEveryTable()
    {
        var factory = await OpenAsync();
        await using var db = await factory.CreateDbContextAsync();

        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'").ToListAsync();

        foreach (var expected in new[] { "Devices", "ShortIds", "TrustedDevices", "AuditLogs", "Sessions", "SessionTokens", "__EFMigrationsHistory" })
            Assert.Contains(expected, tables);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task RunningTheMigrationsAgain_ChangesNothing_AndKeepsTheData()
    {
        var factory = await OpenAsync();
        var device = Device("kept");
        await new PostgresDeviceRegistry(factory).RegisterAsync(device, [4, 2]);

        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();   // a second start
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();

        Assert.Equal("kept", (await new PostgresDeviceRegistry(factory).FindAsync(device.DeviceId))!.DeviceName);
    }

    [PostgresFact]
    public async Task TheModelAndTheMigrations_AreInStep()
    {
        // Fails when someone changes an entity without adding a migration: the next deployment would not get the change.
        var factory = await OpenAsync();
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(db.Database.HasPendingModelChanges(),
            "The model differs from the migrations. Run: dotnet ef migrations add <Name> (in src/server/RemoteDesktop.Server.Persistence).");
    }
}

public sealed class KeyRotationStoreTests : PostgresTestBase
{
    private static KeyRotation Step(DeviceId id, int seq, byte[] oldKey, byte[] newKey) => new()
    {
        DeviceId = id, Sequence = seq, OldKey = oldKey, NewKey = newKey, OldKeySignature = [1], NewKeySignature = [2]
    };

    [PostgresFact]
    public async Task ARotation_ReplacesTheKey_AndTheHistoryComesBackInOrder_AfterARestart()
    {
        var factory = await OpenAsync();
        var registry = new PostgresDeviceRegistry(factory);
        var device = Device();
        await registry.RegisterAsync(device, [1]);

        Assert.True(await registry.TryRotateKeyAsync(Step(device.DeviceId, 0, [1], [2])));
        Assert.True(await registry.TryRotateKeyAsync(Step(device.DeviceId, 1, [2], [3])));

        var after = new PostgresDeviceRegistry(await OpenAsync());
        Assert.Equal(new byte[] { 3 }, await after.GetPublicKeyAsync(device.DeviceId));
        var history = await after.GetRotationsAsync(device.DeviceId);
        Assert.Equal([0, 1], history.Select(h => h.Sequence));
        Assert.Equal(new byte[] { 1 }, history[0].OldKey);
        Assert.Equal(new byte[] { 3 }, history[1].NewKey);
    }

    [PostgresFact]
    public async Task AWrongOldKey_AWrongSequence_AndAnUnknownDevice_ChangeNothing()
    {
        var registry = new PostgresDeviceRegistry(await OpenAsync());
        var device = Device();
        await registry.RegisterAsync(device, [1]);

        Assert.False(await registry.TryRotateKeyAsync(Step(device.DeviceId, 0, [9], [2])));   // not the current key
        Assert.False(await registry.TryRotateKeyAsync(Step(device.DeviceId, 1, [1], [2])));   // skipping a step
        Assert.False(await registry.TryRotateKeyAsync(Step(DeviceId.New(), 0, [1], [2])));    // nobody
        Assert.Equal(new byte[] { 1 }, await registry.GetPublicKeyAsync(device.DeviceId));
        Assert.Empty(await registry.GetRotationsAsync(device.DeviceId));
    }

    [PostgresFact]
    public async Task ManyRotationsRacingForTheSameStep_LetExactlyOneThrough_FromDifferentInstances()
    {
        var a = new PostgresDeviceRegistry(await OpenAsync());
        var b = new PostgresDeviceRegistry(await OpenAsync());
        var device = Device();
        await a.RegisterAsync(device, [1]);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? a : b).TryRotateKeyAsync(Step(device.DeviceId, 0, [1], [(byte)(10 + i)]))));

        Assert.Equal(1, results.Count(r => r));
        var history = await a.GetRotationsAsync(device.DeviceId);
        Assert.Single(history);
        Assert.Equal(history[0].NewKey, await b.GetPublicKeyAsync(device.DeviceId)); // key and history agree
    }

    [PostgresFact]
    public async Task ThereIsALimitToHowManyTimesAKeyCanChange()
    {
        var registry = new PostgresDeviceRegistry(await OpenAsync());
        var device = Device();
        await registry.RegisterAsync(device, [0]);
        for (var i = 0; i < KeyRotation.MaxPerDevice; i++)
            Assert.True(await registry.TryRotateKeyAsync(Step(device.DeviceId, i, [(byte)i], [(byte)(i + 1)])), $"step {i}");
        Assert.False(await registry.TryRotateKeyAsync(Step(device.DeviceId, KeyRotation.MaxPerDevice, [(byte)KeyRotation.MaxPerDevice], [250])));
    }
}
