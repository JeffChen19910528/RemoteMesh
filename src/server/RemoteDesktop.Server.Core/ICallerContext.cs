namespace RemoteDesktop.Server.Core;

/// <summary>What the business layer needs to know about the connection that made a call.</summary>
public interface ICallerContext
{
    string ConnectionId { get; }
    string IpAddress { get; }

    /// <summary>Failed registrations on this connection so far (a connection that keeps failing gets dropped).</summary>
    int RegistrationFailures { get; set; }

    /// <summary>Drops the connection (used to stop credential guessing).</summary>
    void Abort();
}
