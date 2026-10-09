using RemoteDesktop.Input;

namespace RemoteDesktop.Client;

/// <summary>Passes input through unless <paramref name="suspended"/> says it must be held; held input is dropped, not queued.</summary>
internal sealed class SuspendableInput(IInputController inner, Func<bool> suspended) : IInputController
{
    public Task InjectAsync(InputEvent inputEvent, CancellationToken ct = default) =>
        suspended() ? Task.CompletedTask : inner.InjectAsync(inputEvent, ct);
}
