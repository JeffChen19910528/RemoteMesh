namespace RemoteDesktop.Server.Core;

public interface IRateLimiter
{
    // Records an attempt. Returns false if the key has reached maxAttempts within window.
    bool TryConsume(string key, int maxAttempts, TimeSpan window);

    bool IsLockedOut(string key, int maxAttempts, TimeSpan window);

    void Reset(string key);
}
