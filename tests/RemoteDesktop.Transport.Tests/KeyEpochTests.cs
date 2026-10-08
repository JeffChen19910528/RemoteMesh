using System.Security.Cryptography;
using System.Text;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;
using RemoteDesktop.Transport.Secure;

namespace RemoteDesktop.Transport.Tests;

/// <summary>
/// After a reconnect both ends build fresh SecureChannels, which resets their replay windows. Without a new key,
/// everything an attacker recorded before the drop could be replayed into the new connection. Key epochs prevent that.
/// </summary>
public sealed class KeyEpochTests
{
    private readonly SessionId _session = SessionId.New();
    private readonly DeviceKeyPair _a = DeviceKeyGenerator.Generate();
    private readonly DeviceKeyPair _b = DeviceKeyGenerator.Generate();

    private async Task<(AesGcmSessionCrypto alice, AesGcmSessionCrypto bob)> CryptosAsync(int epochAlice, int epochBob)
    {
        var alice = new AesGcmSessionCrypto();
        var bob = new AesGcmSessionCrypto();
        await alice.DeriveSessionKeyAsync(_session, _a.PrivateKey, _b.PublicKey, SessionKeyContext.ForEpoch(epochAlice));
        await bob.DeriveSessionKeyAsync(_session, _b.PrivateKey, _a.PublicKey, SessionKeyContext.ForEpoch(epochBob));
        return (alice, bob);
    }

    private (SecureChannel a, SecureChannel b, LoopbackChannel bobWire) Link(ISessionCrypto alice, ISessionCrypto bob)
    {
        var aw = new LoopbackChannel(ChannelType.Input);
        var bw = new LoopbackChannel(ChannelType.Input);
        aw.PeerWriter = bw.Writer;
        bw.PeerWriter = aw.Writer;
        return (new SecureChannel(aw, alice, _session, true), new SecureChannel(bw, bob, _session, false), bw);
    }

    private static async Task<List<byte[]>> ReadAsync(SecureChannel ch, int count)
    {
        var got = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await foreach (var m in ch.ReceiveAsync(cts.Token))
            {
                got.Add(m.ToArray());
                if (got.Count == count) break;
            }
        }
        catch (OperationCanceledException) { }
        return got;
    }

    [Fact]
    public async Task EpochZero_IsTheOriginalDerivation_SoExistingSessionsAreUnaffected()
    {
        var legacy = new AesGcmSessionCrypto();
        var epoch0 = new AesGcmSessionCrypto();
        await legacy.DeriveSessionKeyAsync(_session, _a.PrivateKey, _b.PublicKey);
        await epoch0.DeriveSessionKeyAsync(_session, _a.PrivateKey, _b.PublicKey, SessionKeyContext.ForEpoch(0));

        var ct = await legacy.EncryptAsync(_session, "hello"u8.ToArray());

        Assert.Equal("hello"u8.ToArray(), await epoch0.DecryptAsync(_session, ct));
    }

    [Fact]
    public async Task DifferentEpochs_DeriveIndependentKeys()
    {
        var (alice0, _) = await CryptosAsync(0, 0);
        var (_, bob1) = await CryptosAsync(1, 1);
        var (alice2, bob2) = await CryptosAsync(2, 2);

        var fromEpoch0 = await alice0.EncryptAsync(_session, "x"u8.ToArray());
        await Assert.ThrowsAnyAsync<CryptographicException>(() => bob1.DecryptAsync(_session, fromEpoch0));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => bob2.DecryptAsync(_session, fromEpoch0));

        var fromEpoch2 = await alice2.EncryptAsync(_session, "y"u8.ToArray());
        Assert.Equal("y"u8.ToArray(), await bob2.DecryptAsync(_session, fromEpoch2));
    }

    [Fact]
    public async Task SidesThatDisagreeOnTheEpoch_CannotTalk()
    {
        var (alice, bob) = await CryptosAsync(3, 4);
        var ct = await alice.EncryptAsync(_session, "x"u8.ToArray());
        await Assert.ThrowsAnyAsync<CryptographicException>(() => bob.DecryptAsync(_session, ct));
    }

    [Fact]
    public async Task ReplayAcrossAReconnect_IsRejected_WhenTheEpochAdvanced()
    {
        // Epoch 0: the attacker records a frame off the wire.
        var (alice0, bob0) = await CryptosAsync(0, 0);
        var (a0, b0, wire0) = Link(alice0, bob0);
        await a0.SendAsync(Encoding.UTF8.GetBytes("transfer $100"), CancellationToken.None);
        await using var tap = wire0.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await tap.MoveNextAsync());
        var recorded = tap.Current.ToArray();

        // The connection drops and is resumed: the server bumps the epoch, both sides derive a new key and new channels.
        var (alice1, bob1) = await CryptosAsync(1, 1);
        var (a1, b1, wire1) = Link(alice1, bob1);
        await wire1.Writer.WriteAsync(recorded); // attacker replays the old frame into the new connection
        await a1.SendAsync("legit message"u8.ToArray(), CancellationToken.None);

        var got = await ReadAsync(b1, 2);
        Assert.Equal("legit message", Encoding.UTF8.GetString(Assert.Single(got)));
        Assert.Equal(1, b1.DroppedFrames); // the recorded frame failed authentication under the new key
    }

    [Fact]
    public async Task ControlExperiment_WithoutAnEpochChange_TheReplayWouldSucceed()
    {
        // Documents WHY the epoch exists: same key + fresh replay window = old traffic is accepted again.
        var (alice, bob) = await CryptosAsync(0, 0);
        var (a0, _, wire0) = Link(alice, bob);
        await a0.SendAsync(Encoding.UTF8.GetBytes("transfer $100"), CancellationToken.None);
        await using var tap = wire0.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await tap.MoveNextAsync());
        var recorded = tap.Current.ToArray();

        var (_, b1, wire1) = Link(alice, bob); // "reconnect" with the same key
        await wire1.Writer.WriteAsync(recorded);

        var got = await ReadAsync(b1, 1);
        Assert.Equal("transfer $100", Encoding.UTF8.GetString(Assert.Single(got)));
    }

    [Fact]
    public async Task ReplacingTheKey_ForANewEpoch_KeepsTheSameCryptoInstanceUsable()
    {
        var crypto = new AesGcmSessionCrypto();
        var peer = new AesGcmSessionCrypto();
        for (var epoch = 0; epoch < 4; epoch++)
        {
            await crypto.DeriveSessionKeyAsync(_session, _a.PrivateKey, _b.PublicKey, SessionKeyContext.ForEpoch(epoch));
            await peer.DeriveSessionKeyAsync(_session, _b.PrivateKey, _a.PublicKey, SessionKeyContext.ForEpoch(epoch));
            var ct = await crypto.EncryptAsync(_session, "e"u8.ToArray());
            Assert.Equal("e"u8.ToArray(), await peer.DecryptAsync(_session, ct));
        }
    }

    [Fact]
    public void NegativeEpoch_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionKeyContext.ForEpoch(-1));

    [Fact]
    public void EpochContexts_AreDistinct()
    {
        var all = Enumerable.Range(0, 100).Select(e => Convert.ToHexString(SessionKeyContext.ForEpoch(e))).ToList();
        Assert.Equal(100, all.Distinct().Count());
    }
}
