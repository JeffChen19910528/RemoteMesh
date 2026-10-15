namespace RemoteDesktop.Protocol.Dtos;

public sealed class AuthResultDto
{
    public string SessionId { get; set; } = "";
    public string SessionToken { get; set; } = "";
    public string State { get; set; } = "";
    public long GrantedPermissions { get; set; }
}
