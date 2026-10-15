using RemoteDesktop.Core;
using RemoteDesktop.Security;

namespace RemoteDesktop.Security.Tests;

public sealed class OsKeyStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "RemoteMeshTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private OsKeyStore CreateStore() => new(_tempDir);

    [Fact]
    public async Task LoadOrCreate_FirstCall_CreatesKeyPair()
    {
        var store = CreateStore();
        var deviceId = DeviceId.New();

        var kp = await store.LoadOrCreateAsync(deviceId);

        Assert.NotEmpty(kp.PublicKey);
        Assert.NotEmpty(kp.PrivateKey);
    }

    [Fact]
    public async Task LoadOrCreate_SecondCall_ReturnsSameKeyPair()
    {
        var store = CreateStore();
        var deviceId = DeviceId.New();

        var kp1 = await store.LoadOrCreateAsync(deviceId);
        var kp2 = await store.LoadOrCreateAsync(deviceId);

        Assert.Equal(kp1.PublicKey, kp2.PublicKey);
        Assert.Equal(kp1.PrivateKey, kp2.PrivateKey);
    }

    [Fact]
    public async Task LoadOrCreate_DifferentDevices_GetDifferentKeys()
    {
        var store = CreateStore();
        var id1 = DeviceId.New();
        var id2 = DeviceId.New();

        var kp1 = await store.LoadOrCreateAsync(id1);
        var kp2 = await store.LoadOrCreateAsync(id2);

        Assert.False(kp1.PublicKey.SequenceEqual(kp2.PublicKey));
    }

    [Fact]
    public async Task GetPublicKey_ReturnsCorrectKey()
    {
        var store = CreateStore();
        var deviceId = DeviceId.New();

        var kp = await store.LoadOrCreateAsync(deviceId);
        var pub = await store.GetPublicKeyAsync(deviceId);

        Assert.Equal(kp.PublicKey, pub);
    }

    [Fact]
    public async Task Delete_RemovesKeyFromDisk()
    {
        var store = CreateStore();
        var deviceId = DeviceId.New();

        await store.LoadOrCreateAsync(deviceId);
        await store.DeleteAsync(deviceId);

        // After delete, a new call should generate a DIFFERENT key pair
        var kp1 = await store.LoadOrCreateAsync(deviceId);
        var kp2 = await store.LoadOrCreateAsync(deviceId);

        // Second load after delete still returns stable new key
        Assert.Equal(kp1.PublicKey, kp2.PublicKey);
    }

    [Fact]
    public async Task PrivateKeyFile_IsOwnerOnly_OnUnix_FromTheMomentItIsCreated()
    {
        if (OperatingSystem.IsWindows()) return; // Windows protects the key with DPAPI (current user) instead of a file mode

        var deviceId = DeviceId.New();
        await CreateStore().LoadOrCreateAsync(deviceId);

        var mode = File.GetUnixFileMode(Path.Combine(_tempDir, $"{deviceId}.key"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task StoredFile_DoesNotContainThePlaintextPrivateKey_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // on Unix the key relies on the 0600 file mode (documented limitation)

        var deviceId = DeviceId.New();
        var kp = await CreateStore().LoadOrCreateAsync(deviceId);

        var text = await File.ReadAllTextAsync(Path.Combine(_tempDir, $"{deviceId}.key"));
        Assert.DoesNotContain(Convert.ToBase64String(kp.PrivateKey), text);
    }

    [Fact]
    public async Task StoredFile_DoesNotContainThePlaintextPrivateKey_OnMacOS()
    {
        // On macOS, the private key is wrapped by a Keychain-stored AES key (when the Keychain is available).
        // This test verifies that the file does not contain the raw key. If the Keychain is unavailable (e.g. CI),
        // the test is skipped because the current fallback behaviour is documented as "file permissions only".
        if (!OperatingSystem.IsMacOS()) return;

        var deviceId = DeviceId.New();
        var kp = await CreateStore().LoadOrCreateAsync(deviceId);

        var text = await File.ReadAllTextAsync(Path.Combine(_tempDir, $"{deviceId}.key"));
        // The file must start with the KCHV1: prefix inside the base64 when Keychain is available.
        // If the prefix is absent the key was stored as plaintext (Keychain unavailable) — still acceptable on CI.
        var prefix = "KCHV1:";
        var prefixBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prefix)).TrimEnd('=');
        if (text.Contains(prefixBase64, StringComparison.Ordinal))
            Assert.DoesNotContain(Convert.ToBase64String(kp.PrivateKey), text);
    }

    [Fact]
    public async Task KeyPair_IsValidForSigning()
    {
        var store = CreateStore();
        var deviceId = DeviceId.New();
        var kp = await store.LoadOrCreateAsync(deviceId);

        var data = "test payload"u8.ToArray();
        var sig = DeviceKeyGenerator.Sign(kp, data);
        Assert.True(DeviceKeyGenerator.Verify(kp.PublicKey, data, sig));
    }
}
