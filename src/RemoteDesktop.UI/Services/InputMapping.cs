using System.Windows.Input;

namespace RemoteDesktop.UI.Services;

/// <summary>WPF key → Windows virtual-key code (what the host injects).</summary>
public static class KeyMap
{
    /// <summary>The virtual-key code for the key, or 0 when it has no usable code.</summary>
    public static int ToVirtualKey(KeyEventArgsLike e)
    {
        // Alt combinations and F10 arrive as Key.System with the real key in SystemKey; IME input as ImeProcessed.
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };
        if (key == Key.None) return 0;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        return vk is > 0 and <= 0xFE ? vk : 0;
    }

    public static int ToButton(MouseButton button) => button switch
    {
        MouseButton.Left => 1,
        MouseButton.Right => 2,
        MouseButton.Middle => 3,
        _ => 0 // extra buttons are not forwarded
    };
}

/// <summary>The few facts about a key event the mapping needs, so tests need no real WPF event.</summary>
public readonly record struct KeyEventArgsLike(Key Key, Key SystemKey, Key ImeProcessedKey, Key DeadCharProcessedKey)
{
    public static KeyEventArgsLike From(KeyEventArgs e) => new(e.Key, e.SystemKey, e.ImeProcessedKey, e.DeadCharProcessedKey);
    public static KeyEventArgsLike Plain(Key key) => new(key, Key.None, Key.None, Key.None);
}
