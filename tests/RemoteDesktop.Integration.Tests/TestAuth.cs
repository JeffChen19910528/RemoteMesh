using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;

namespace RemoteDesktop.Integration.Tests;

/// <summary>
/// Test devices own real key pairs and register through the real challenge-response, exactly like a client.
/// </summary>
public static class TestAuth
{
    private static readonly ConcurrentDictionary<string, DeviceKeyPair> Keys = new();

    public static DeviceInfoDto MakeDevice(string name)
    {
        var keys = DeviceKeyGenerator.Generate();
        var id = DeviceId.New().ToString();
        Keys[id] = keys;
        return new DeviceInfoDto
        {
            DeviceId = id,
            DeviceName = name,
            Platform = "Windows",
            PublicKeyBase64 = Convert.ToBase64String(keys.PublicKey)
        };
    }

    public static DeviceKeyPair KeysOf(string deviceId) => Keys[deviceId];

    public static IDeviceSigner SignerFor(string deviceId) => new DeviceKeyPairSigner(Keys[deviceId]);

    /// <summary>BeginRegistration → sign the challenge → RegisterDevice.</summary>
    public static async Task RegisterAsync(HubConnection connection, DeviceInfoDto dto) =>
        await RegisterAsync(connection, dto, Keys[dto.DeviceId]);

    /// <summary>Registers <paramref name="dto"/> but signs with <paramref name="signingKeys"/> (to test impostors).</summary>
    public static async Task RegisterAsync(HubConnection connection, DeviceInfoDto dto, DeviceKeyPair signingKeys)
    {
        var challenge = Convert.FromBase64String(await connection.InvokeAsync<string>("BeginRegistration"));
        var signature = SignPayload(signingKeys, challenge, dto.DeviceId, connection.ConnectionId!);
        await connection.InvokeAsync("RegisterDevice", dto, signature);
    }

    public static string SignPayload(DeviceKeyPair keys, byte[] challenge, string deviceId, string connectionId) =>
        Convert.ToBase64String(DeviceKeyGenerator.Sign(keys, RegistrationProof.BuildPayload(challenge, deviceId, connectionId)));
}
