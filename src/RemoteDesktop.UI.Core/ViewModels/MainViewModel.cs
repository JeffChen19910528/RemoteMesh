using System.Collections.ObjectModel;
using System.Windows.Input;
using RemoteDesktop.Client;
using RemoteDesktop.Clipboard;
using RemoteDesktop.Core;
using RemoteDesktop.Platform;
using RemoteDesktop.Protocol;
using RemoteDesktop.UI.Infrastructure;
using RemoteDesktop.UI.Services;

namespace RemoteDesktop.UI.ViewModels;

/// <summary>One computer currently controlling this one.</summary>
public sealed class BeingControlledItem(HostSession session) : ObservableObject
{
    public HostSession Session { get; } = session;

    /// <summary>The same digits show on the other person's screen; reading them to each other rules out someone in between.</summary>
    public string SecurityCode => Session.SecurityCode;
    public string Trust => Session.PeerVerified ? "✓ 已核對的裝置"
        : Session.PeerIsNew ? "第一次連線,請核對安全碼" : "尚未核對安全碼";
    public bool CanVerify => !Session.PeerVerified;

    public void Verify()
    {
        if (!Session.MarkPeerVerified()) return;
        Raise(nameof(Trust));
        Raise(nameof(CanVerify));
    }

    public string Who =>
        Session.PeerShortId is { } shortId ? $"裝置 {ShortId.Format(shortId)}"
        : Session.Peer is { } id ? Short(id)
        : "透過一次性密碼或信任裝置連線的對象";
    public string Rights => PermissionOptions.Describe(Session.Granted);
    public string Since => Session.StartedAt.ToLocalTime().ToString("HH:mm:ss");

    private static string Short(DeviceId id) => id.ToString()[..8] + "…";
}

/// <summary>A device this computer refuses to be controlled by.</summary>
public sealed class BlockedPeerItem(BlockedPeer peer)
{
    public DeviceId Device { get; } = peer.Device;
    public string Who => peer.ShortId is { } shortId ? $"裝置 {ShortId.Format(shortId)}" : peer.Device.ToString()[..8] + "…";
    public string Since => peer.Since.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly IShell _shell;
    private readonly SettingsStore _store;
    private readonly Func<ClientOptions, HostServices, IApprovalHandler, RemoteMeshClient> _clientFactory;
    private readonly SynchronizationContext _ui;
    private readonly Func<AppSettings, HostServices> _hostServices;
    private readonly Func<IViewerRenderer> _rendererFactory;
    private CancellationTokenSource _lifetime = new();
    private RemoteMeshClient? _client;
    private AppSettings _settings;
    private string _status = "正在啟動…";
    private string _myId = "—";
    private string _myDeviceId = string.Empty;
    private string _otp = string.Empty;
    private string _remoteId = string.Empty;
    private string _remoteCode = string.Empty;
    private string _error = string.Empty;
    private bool _online;
    private bool _connecting;

    public MainViewModel(IShell shell, SettingsStore store, Func<IViewerRenderer> rendererFactory,
        Func<ClientOptions, HostServices, IApprovalHandler, RemoteMeshClient>? clientFactory = null,
        SynchronizationContext? ui = null,
        Func<AppSettings, HostServices>? hostServices = null)
    {
        _hostServices = hostServices ?? DefaultHostServices;
        _shell = shell;
        _store = store;
        _rendererFactory = rendererFactory;
        _ui = ui ?? SynchronizationContext.Current ?? new SynchronizationContext();
        _clientFactory = clientFactory ?? ((o, h, a) => new RemoteMeshClient(o, h, a));
        _settings = store.Load();

        ConnectCommand = new RelayCommand(_ => ConnectAsync(), _ => _online && !_connecting);
        NewCodeCommand = new RelayCommand(_ => NewCodeAsync(), _ => _online);
        CopyIdCommand = new RelayCommand(_ => { if (_client is not null) _shell.CopyToClipboard(MyId); }, _ => _online);
        SettingsCommand = new RelayCommand(_ => OpenSettingsAsync(null));
        RotateKeyCommand = new RelayCommand(_ => RotateKeyAsync(), _ => _online && !_connecting && BeingControlled.Count == 0);
        DisconnectCommand = new RelayCommand(p => DisconnectAsync(p as BeingControlledItem), p => p is BeingControlledItem);
        BlockCommand = new RelayCommand(p => BlockAsync(p as BeingControlledItem), p => p is BeingControlledItem);
        UnblockCommand = new RelayCommand(p => UnblockAsync(p as BlockedPeerItem), p => p is BlockedPeerItem);
        VerifyCommand = new RelayCommand(p => { (p as BeingControlledItem)?.Verify(); Refresh(); }, p => p is BeingControlledItem { CanVerify: true });
        // Toolkits without an automatic re-query (Avalonia) only ask again when told: tell them whenever the answer can change.
        BeingControlled.CollectionChanged += (_, _) => Refresh();
    }

    public ObservableCollection<BeingControlledItem> BeingControlled { get; } = new();
    public ICommand VerifyCommand { get; }
    public ICommand BlockCommand { get; }
    public ICommand UnblockCommand { get; }

    /// <summary>The devices that cannot control this computer (kept on disk).</summary>
    public ObservableCollection<BlockedPeerItem> BlockedPeers { get; } = new();
    public bool HasBlockedPeers => BlockedPeers.Count > 0;

    private void RefreshBlocked()
    {
        BlockedPeers.Clear();
        if (_client is not null)
            foreach (var peer in _client.BlockedPeers) BlockedPeers.Add(new BlockedPeerItem(peer));
        Raise(nameof(HasBlockedPeers));
    }

    /// <summary>Ends this device's session and refuses it from now on, until the person lifts the block.</summary>
    public async Task BlockAsync(BeingControlledItem? item)
    {
        if (item is null || _client is null) return;
        if (!await _shell.ConfirmAsync("要封鎖這個裝置嗎?\n\n它的連線會立刻結束,之後它再怎麼要求(即使是信任裝置或一次性密碼)也會被直接拒絕,不會再問你。可以在「已封鎖的裝置」解除。")) return;
        await _client.BlockPeerAsync(item.Session.PeerDeviceId, item.Session.PeerShortId);
        RefreshBlocked();
    }

    public Task UnblockAsync(BlockedPeerItem? item)
    {
        if (item is not null && _client?.UnblockPeer(item.Device) == true) RefreshBlocked();
        return Task.CompletedTask;
    }

    public ICommand ConnectCommand { get; }
    public ICommand NewCodeCommand { get; }
    public ICommand CopyIdCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand RotateKeyCommand { get; }

    /// <summary>Gives this computer a new security key (after a suspected leak). Computers that know it follow by themselves.</summary>
    public async Task RotateKeyAsync()
    {
        if (_client is null) return;
        if (!await _shell.ConfirmAsync("要更換這台電腦的安全金鑰嗎?\n\n用過這台電腦的人會自動跟著換,但安全碼會變,需要重新核對。更換期間不能有人正在連線。")) return;
        Error = string.Empty;
        try
        {
            await _client.RotateKeyAsync(_lifetime.Token);
            Status = "已更換安全金鑰";
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException)
        {
            Error = "目前無法更換金鑰:請先結束所有連線後再試。";
        }
        catch (Exception ex)
        {
            Error = $"更換金鑰失敗({ex.GetType().Name})";
        }
    }
    public ICommand DisconnectCommand { get; }

    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>The number to give people, grouped for reading: <c>482 913 057</c>.</summary>
    public string MyId { get => _myId; private set => Set(ref _myId, value); }

    /// <summary>The full device ID (also accepted when connecting; mostly useful for support).</summary>
    public string MyDeviceId { get => _myDeviceId; private set => Set(ref _myDeviceId, value); }
    public string Otp { get => _otp; private set => Set(ref _otp, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }

    public string RemoteId
    {
        get => _remoteId;
        set { if (Set(ref _remoteId, value)) Error = string.Empty; }
    }

    public string RemoteCode { get => _remoteCode; set => Set(ref _remoteCode, value); }

    public bool IsOnline
    {
        get => _online;
        private set
        {
            if (!Set(ref _online, value)) return;
            Refresh();
        }
    }

    public bool IsConnecting { get => _connecting; private set { if (Set(ref _connecting, value)) Refresh(); } }

    private void Refresh()
    {
        ((RelayCommand)ConnectCommand).Refresh();
        ((RelayCommand)NewCodeCommand).Refresh();
        ((RelayCommand)CopyIdCommand).Refresh();
        ((RelayCommand)RotateKeyCommand).Refresh();
        ((RelayCommand)VerifyCommand).Refresh();
    }

    // ── Starting up ──────────────────────────────────────────────────────

    /// <summary>Opens the settings if they are not usable yet, then connects to the server (retrying until it works).</summary>
    public async Task StartAsync()
    {
        var problem = _settings.Validate();
        if (problem is not null && !await OpenSettingsAsync(string.IsNullOrWhiteSpace(_settings.ServerUrl) ? null : problem))
        {
            Status = "尚未設定伺服器";
            return;
        }
        await RunClientAsync();
    }

    private async Task RunClientAsync()
    {
        _lifetime.Cancel();
        _lifetime = new CancellationTokenSource();
        var ct = _lifetime.Token;
        if (_client is not null) { await _client.DisposeAsync(); _client = null; }
        IsOnline = false;
        BeingControlled.Clear();

        while (!ct.IsCancellationRequested)
        {
            var client = _clientFactory(
                new ClientOptions
                {
                    ServerUri = _settings.ServerUri,
                    DeviceName = _settings.DeviceName,
                    DataDirectory = _store.DataDirectory,
                    JpegCodec = PlatformHost.CreateJpegCodec(),
                    VideoBudgetKbps = _settings.VideoBudgetKbps
                },
                _hostServices(_settings),
                new UiApprovalHandler(_shell));
            try
            {
                Status = "正在連線到伺服器…";
                // Subscribe before starting so a connection that arrives immediately is not missed.
                client.HostSessionStarted += OnHostSessionStarted;
                client.Error += (what, ex) => Post(() => Error = ex is PeerKeyChangedException
                    ? "有一個連線進來的裝置,其安全金鑰與上次不同,已拒絕該連線。這可能代表有人在竄改連線。"
                    : $"{what} 發生錯誤({ex.GetType().Name})");
                await client.StartAsync(ct);
                _client = client;
                MyId = ShortId.Format(client.ShortId);
                RefreshBlocked();
                MyDeviceId = client.DeviceId.ToString();
                Status = "已連線,可以接受遠端連線";
                IsOnline = true;
                await NewCodeAsync();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await client.DisposeAsync();
                return;
            }
            catch (Exception ex)
            {
                await client.DisposeAsync();
                Status = $"無法連線到伺服器({ex.GetType().Name}),{RetryDelay.TotalSeconds:0} 秒後重試";
                try { await Task.Delay(RetryDelay, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>This computer's real screen, input and clipboard, as the settings allow.</summary>
    private static HostServices DefaultHostServices(AppSettings settings)
    {
        var fileRoot = string.IsNullOrWhiteSpace(settings.FileRoot) ? null : settings.FileRoot;
        return PlatformHost.CreateHostServices(settings.AllowClipboard, fileRoot).Services;
    }

    // ── Actions ──────────────────────────────────────────────────────────

    public async Task ConnectAsync()
    {
        if (_client is null) return;
        if (!ConnectionAddress.TryNormalize(RemoteId, out var target))
        {
            Error = "對方的 ID 格式不正確。請輸入對方畫面上顯示的 9 位數字(例如 482 913 057)。";
            return;
        }
        if (target == _client.ShortId || target == _client.DeviceId.ToString())
        {
            Error = "不能連線到自己。";
            return;
        }

        var code = RemoteCode.Trim();
        Error = string.Empty;
        IsConnecting = true;
        Status = code.Length > 0 ? "正在以一次性密碼連線…" : "等待對方同意…";
        try
        {
            var renderer = _rendererFactory();
            var session = await _client.ConnectAsync(target, renderer, code.Length > 0 ? code : null, cancellationToken: _lifetime.Token);
            _shell.ShowViewer(session, renderer);
            RemoteCode = string.Empty;
            Status = "已連線,可以接受遠端連線";
        }
        catch (ConnectionRefusedException ex)
        {
            Error = ex.Message;
            Status = "已連線,可以接受遠端連線";
        }
        catch (PeerKeyChangedException)
        {
            // Not a glitch: the server presented another key than the one this computer saw before.
            Error = "對方的安全金鑰與上次不同,連線已中止。這可能代表有人在竄改連線;請用電話等其他管道向對方確認後再試。";
            Status = "已連線,可以接受遠端連線";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = $"連線失敗({ex.GetType().Name})";
            Status = "已連線,可以接受遠端連線";
        }
        finally { IsConnecting = false; }
    }

    public async Task NewCodeAsync()
    {
        if (_client is null) return;
        try { Otp = await _client.GenerateOtpAsync(_lifetime.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Error = "無法產生一次性密碼"; }
    }

    public async Task<bool> OpenSettingsAsync(string? problem)
    {
        var edited = await _shell.EditSettingsAsync(_settings, problem);
        if (edited is null) return false;
        _store.Save(edited);
        var serverChanged = edited.ServerUrl != _settings.ServerUrl || edited.DeviceName != _settings.DeviceName;
        _settings = edited;
        // Host options (clipboard, folder) are read when the client is built, so rebuild it to apply them.
        if (_client is not null || serverChanged) _ = RunClientAsync();
        return true;
    }

    private async Task DisconnectAsync(BeingControlledItem? item)
    {
        if (item is not null) await item.Session.DisposeAsync();
    }

    private void OnHostSessionStarted(HostSession session) => Post(() =>
    {
        var item = new BeingControlledItem(session);
        BeingControlled.Add(item);
        session.Ended += _ => Post(() => BeingControlled.Remove(item));
    });

    private void Post(Action action) => _ui.Post(_ => action(), null);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_client is not null) await _client.DisposeAsync();
        _lifetime.Dispose();
    }
}
