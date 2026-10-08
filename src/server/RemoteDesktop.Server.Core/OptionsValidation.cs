namespace RemoteDesktop.Server.Core;

public static class OptionsValidation
{
    public static void ThrowIfAny(IReadOnlyCollection<string> errors)
    {
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid configuration: " + string.Join(" ", errors));
    }
}
