using System.IO;
using System.Windows.Input;
using RemoteDesktop.Client;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.FileTransfer;
using RemoteDesktop.UI.Services;

namespace RemoteDesktop.UI.Tests;

public sealed class CoordinateMapperTests
{
    // A 1000x500 picture (2:1) shown in a 1000x1000 view is letterboxed: 250 px bars above and below.
    [Fact]
    public void PointsInsideThePicture_AreNormalizedToTheRemoteScreen()
    {
        Assert.Equal((0, 0), CoordinateMapper.Map(0, 250, 1000, 1000, 1000, 500));
        Assert.Equal((32767, 32767), CoordinateMapper.Map(1000, 750, 1000, 1000, 1000, 500));
        var middle = CoordinateMapper.Map(500, 500, 1000, 1000, 1000, 500)!.Value;
        Assert.InRange(middle.X, 16383, 16384);
        Assert.InRange(middle.Y, 16383, 16384);
    }

    [Theory]
    [InlineData(500, 100)]   // top bar
    [InlineData(500, 900)]   // bottom bar
    [InlineData(-5, 500)]
    [InlineData(1200, 500)]
    public void PointsOnTheBars_AreNotOnTheRemoteScreen(double x, double y)
    {
        Assert.Null(CoordinateMapper.Map(x, y, 1000, 1000, 1000, 500));
    }

    [Fact]
    public void WideView_PillarboxesTheSides()
    {
        // 500x500 picture in a 1000x500 view: 250 px bars left and right.
        Assert.Null(CoordinateMapper.Map(100, 250, 1000, 500, 500, 500));
        Assert.Equal((0, 16384), CoordinateMapper.Map(250, 250, 1000, 500, 500, 500));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 100, 100, 0)]
    public void DegenerateSizes_GiveNothing(double viewW, double viewH, int frameW, int frameH)
    {
        Assert.Null(CoordinateMapper.Map(10, 10, viewW, viewH, frameW, frameH));
    }

    [Fact]
    public void Results_NeverLeaveTheWireRange()
    {
        var rnd = new Random(1);
        for (var i = 0; i < 2000; i++)
        {
            var r = CoordinateMapper.Map(rnd.NextDouble() * 1400 - 200, rnd.NextDouble() * 1400 - 200, 1000, 1000, 1920, 1080);
            if (r is { } p) { Assert.InRange(p.X, 0, 32767); Assert.InRange(p.Y, 0, 32767); }
        }
    }
}

public sealed class KeyMapTests
{
    [Theory]
    [InlineData(Key.A, 0x41)]
    [InlineData(Key.D5, 0x35)]
    [InlineData(Key.Enter, 0x0D)]
    [InlineData(Key.Escape, 0x1B)]
    [InlineData(Key.Left, 0x25)]
    [InlineData(Key.F5, 0x74)]
    [InlineData(Key.LeftCtrl, 0xA2)]
    [InlineData(Key.RightAlt, 0xA5)]
    public void OrdinaryKeys_MapToTheirVirtualKey(Key key, int expected) =>
        Assert.Equal(expected, KeyMap.ToVirtualKey(KeyEventArgsLike.Plain(key)));

    [Fact]
    public void AltCombinations_UseTheRealKey_NotKeySystem()
    {
        // Alt+F4 arrives as Key.System with the real key in SystemKey.
        Assert.Equal(0x73, KeyMap.ToVirtualKey(new KeyEventArgsLike(Key.System, Key.F4, Key.None, Key.None)));
    }

    [Fact]
    public void ImeProcessedKeys_UseTheUnderlyingKey()
    {
        Assert.Equal(0x41, KeyMap.ToVirtualKey(new KeyEventArgsLike(Key.ImeProcessed, Key.None, Key.A, Key.None)));
    }

    [Fact]
    public void KeysWithoutACode_AreDropped()
    {
        Assert.Equal(0, KeyMap.ToVirtualKey(KeyEventArgsLike.Plain(Key.None)));
        Assert.Equal(0, KeyMap.ToVirtualKey(new KeyEventArgsLike(Key.System, Key.None, Key.None, Key.None)));
    }

    [Theory]
    [InlineData(MouseButton.Left, 1)]
    [InlineData(MouseButton.Right, 2)]
    [InlineData(MouseButton.Middle, 3)]
    [InlineData(MouseButton.XButton1, 0)]
    [InlineData(MouseButton.XButton2, 0)]
    public void MouseButtons_MapToTheWireNumbers(MouseButton button, int expected) =>
        Assert.Equal(expected, KeyMap.ToButton(button));
}

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rm-ui-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static AppSettings Good() => new() { ServerUrl = "https://relay.example.com", DeviceName = "My PC" };

    [Fact]
    public void ReasonableSettings_AreValid() => Assert.Null(Good().Validate());

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://relay.example.com")]
    [InlineData("relay.example.com")]
    public void BadServerUrls_AreRejected(string url)
    {
        var s = Good();
        s.ServerUrl = url;
        Assert.NotNull(s.Validate());
    }

    [Fact]
    public void PlainHttp_IsOnlyForThisComputer()
    {
        var remote = Good();
        remote.ServerUrl = "http://relay.example.com";
        Assert.NotNull(remote.Validate());

        var local = Good();
        local.ServerUrl = "http://127.0.0.1:5222";
        Assert.Null(local.Validate());
    }

    [Fact]
    public void RelativeFolder_AndBadName_AreRejected()
    {
        var a = Good(); a.FileRoot = "files";
        var b = Good(); b.DeviceName = " ";
        var c = Good(); c.DeviceName = new string('x', 65);
        Assert.NotNull(a.Validate());
        Assert.NotNull(b.Validate());
        Assert.NotNull(c.Validate());
    }

    [Fact]
    public void ServerUri_HasExactlyOneTrailingSlash()
    {
        var s = Good();
        s.ServerUrl = " https://relay.example.com/// ";
        Assert.Equal("https://relay.example.com/", s.ServerUri.ToString());
    }

    [Fact]
    public void Settings_RoundTrip_ThroughTheStore()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { ServerUrl = "https://a.example", DeviceName = "Desk", AllowClipboard = true, FileRoot = @"C:\share" });

        var loaded = new SettingsStore(_dir).Load();
        Assert.Equal("https://a.example", loaded.ServerUrl);
        Assert.Equal("Desk", loaded.DeviceName);
        Assert.True(loaded.AllowClipboard);
        Assert.Equal(@"C:\share", loaded.FileRoot);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // no half-written leftovers
    }

    [Fact]
    public void MissingOrDamagedFile_FallsBackToDefaults_InsteadOfStoppingTheApp()
    {
        var store = new SettingsStore(_dir);
        Assert.Equal(string.Empty, store.Load().ServerUrl);

        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ this is not json");
        Assert.Equal(string.Empty, store.Load().ServerUrl);
    }

    [Fact]
    public void ClipboardSharing_IsOffByDefault() => Assert.False(new AppSettings().AllowClipboard);
}

public sealed class PermissionOptionsTests
{
    [Fact]
    public void OnlyViewing_IsPreselected_AndItCannotBeUnticked()
    {
        var on = PermissionOptions.All.Where(o => o.DefaultOn).ToList();
        Assert.Equal(Permission.ViewScreen, Assert.Single(on).Permission);
        Assert.True(on[0].Locked);
    }

    [Fact]
    public void RestartAndShutdown_AreNeverOffered()
    {
        var offered = PermissionOptions.All.Aggregate(Permission.None, (a, o) => a | o.Permission);
        Assert.Equal(Permission.FullControl, offered);
        Assert.Equal(Permission.None, offered & (Permission.RemoteRestart | Permission.RemoteShutdown));
    }

    [Fact]
    public void Describe_ListsExactlyWhatWasGranted()
    {
        Assert.Equal("查看畫面", PermissionOptions.Describe(Permission.ViewOnly));
        var text = PermissionOptions.Describe(Permission.ViewScreen | Permission.ControlMouse);
        Assert.Contains("查看畫面", text);
        Assert.Contains("控制滑鼠", text);
        Assert.DoesNotContain("控制鍵盤", text);
        Assert.Equal("無", PermissionOptions.Describe(Permission.None));
    }
}

public sealed class ResultTextTests
{
    [Fact]
    public void EveryResult_HasPlainWording_NeverAnEnumName()
    {
        foreach (var r in Enum.GetValues<ClipboardResult>())
        {
            var t = ResultText.Of(r);
            Assert.False(string.IsNullOrWhiteSpace(t));
            Assert.DoesNotContain(r.ToString(), t);
        }
        foreach (var r in Enum.GetValues<FileTransferResult>())
        {
            var t = ResultText.Of(r);
            Assert.False(string.IsNullOrWhiteSpace(t));
            Assert.DoesNotContain(r.ToString(), t);
        }
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void Sizes_AreReadable(long bytes, string expected) => Assert.Equal(expected, ResultText.Size(bytes));
}

public sealed class UiApprovalHandlerTests
{
    private sealed class FakeShell : IShell
    {
        public List<TaskCompletionSource<ApprovalDecision>> Open { get; } = new();
        public List<ApprovalRequest> Asked { get; } = new();

        public Task<ApprovalDecision> AskApprovalAsync(ApprovalRequest request, TimeSpan timeout, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Open) { Open.Add(tcs); Asked.Add(request); }
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public void ShowViewer(ViewerSession session, IViewerRenderer renderer) { }
        public Task<AppSettings?> EditSettingsAsync(AppSettings current, string? problem) => Task.FromResult<AppSettings?>(null);
        public bool ConfirmAnswer { get; set; } = true;
        public Task<bool> ConfirmAsync(string message) => Task.FromResult(ConfirmAnswer);

        public void CopyToClipboard(string text) { }
    }

    private static ApprovalRequest Req() => new(SessionId.New(), DeviceId.New());

    [Fact]
    public async Task RequestsAreAskedOneAtATime_InOrder()
    {
        var shell = new FakeShell();
        var handler = new UiApprovalHandler(shell);

        var first = handler.DecideAsync(Req(), CancellationToken.None);
        var second = handler.DecideAsync(Req(), CancellationToken.None);
        await Task.Delay(150);
        Assert.Single(shell.Asked); // the second person is not shown until the first is answered

        shell.Open[0].SetResult(new ApprovalDecision(true, Permission.ViewOnly));
        Assert.True((await first).Accept);

        await Task.Delay(150);
        Assert.Equal(2, shell.Asked.Count);
        shell.Open[1].SetResult(ApprovalDecision.Deny);
        Assert.False((await second).Accept);
    }

    [Fact]
    public async Task AWithdrawnRequest_WaitingItsTurn_IsNeverShown()
    {
        var shell = new FakeShell();
        var handler = new UiApprovalHandler(shell);
        var first = handler.DecideAsync(Req(), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var second = handler.DecideAsync(Req(), cts.Token);
        await Task.Delay(100);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        shell.Open[0].SetResult(ApprovalDecision.Deny);
        await first;
        await Task.Delay(100);
        Assert.Single(shell.Asked); // nobody was asked about the request whose sender had left
    }

    [Fact]
    public async Task AWithdrawnRequest_BeingShown_IsCancelled_AndTheNextOneProceeds()
    {
        var shell = new FakeShell();
        var handler = new UiApprovalHandler(shell);
        using var cts = new CancellationTokenSource();
        var first = handler.DecideAsync(Req(), cts.Token);
        var second = handler.DecideAsync(Req(), CancellationToken.None);
        await Task.Delay(100);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        await Task.Delay(150);
        Assert.Equal(2, shell.Asked.Count); // the prompt slot was released
        shell.Open[1].SetResult(ApprovalDecision.Deny);
        await second;
    }
}
