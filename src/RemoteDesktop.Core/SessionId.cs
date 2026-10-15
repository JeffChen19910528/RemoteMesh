namespace RemoteDesktop.Core;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
    public static SessionId Parse(string value) => new(Guid.Parse(value));
    public static bool TryParse(string value, out SessionId result)
    {
        if (Guid.TryParse(value, out var guid)) { result = new SessionId(guid); return true; }
        result = default;
        return false;
    }
    public override string ToString() => Value.ToString("D");
}
