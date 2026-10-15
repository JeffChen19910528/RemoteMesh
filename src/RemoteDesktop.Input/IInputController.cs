namespace RemoteDesktop.Input;

public interface IInputController
{
    Task InjectAsync(InputEvent inputEvent, CancellationToken ct = default);
}
