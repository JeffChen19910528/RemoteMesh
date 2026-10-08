namespace RemoteDesktop.Core;

[Flags]
public enum Permission
{
    None             = 0,
    ViewScreen       = 1 << 0,
    ControlMouse     = 1 << 1,
    ControlKeyboard  = 1 << 2,
    ClipboardRead    = 1 << 3,
    ClipboardWrite   = 1 << 4,
    FileUpload       = 1 << 5,
    FileDownload     = 1 << 6,
    RemoteRestart    = 1 << 7,
    RemoteShutdown   = 1 << 8,

    ViewOnly = ViewScreen,
    FullControl = ViewScreen | ControlMouse | ControlKeyboard |
                  ClipboardRead | ClipboardWrite | FileUpload | FileDownload
}

public sealed class PermissionSet
{
    private Permission _granted;

    public PermissionSet(Permission initial = Permission.ViewOnly) => _granted = initial;

    public bool Has(Permission p) => (_granted & p) == p;
    public void Grant(Permission p) => _granted |= p;
    public void Revoke(Permission p) => _granted &= ~p;
    public Permission Granted => _granted;

    public static PermissionSet ViewOnly() => new(Permission.ViewOnly);
    public static PermissionSet FullControl() => new(Permission.FullControl);
}
