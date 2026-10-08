namespace RemoteDesktop.Input;

public enum InputEventType
{
    MouseMove,
    MouseButtonDown,
    MouseButtonUp,
    MouseWheel,
    KeyDown,
    KeyUp
}

public sealed class InputEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required InputEventType EventType { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Button { get; init; }
    public int Delta { get; init; }
    public int KeyCode { get; init; }
    public int Modifiers { get; init; }
}
