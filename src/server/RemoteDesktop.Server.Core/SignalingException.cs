namespace RemoteDesktop.Server.Core;

/// <summary>
/// A refusal whose message is safe to show the caller. The transport layer turns it into the protocol's error
/// type; detail that must not reach the caller goes to the audit log instead.
/// </summary>
public sealed class SignalingException(string message) : Exception(message);
