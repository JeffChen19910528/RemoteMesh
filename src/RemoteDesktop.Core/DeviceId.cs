namespace RemoteDesktop.Core;

public readonly record struct DeviceId(Guid Value)
{
    public static DeviceId New() => new(Guid.NewGuid());
    public static DeviceId Parse(string value) => new(Guid.Parse(value));
    public static bool TryParse(string value, out DeviceId result)
    {
        if (Guid.TryParse(value, out var guid)) { result = new DeviceId(guid); return true; }
        result = default;
        return false;
    }
    public override string ToString() => Value.ToString("D");
}
