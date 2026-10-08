using RemoteDesktop.Core;

namespace RemoteDesktop.Core.Tests;

public sealed class PermissionSetTests
{
    [Fact]
    public void ViewOnly_HasViewScreenOnly()
    {
        var ps = PermissionSet.ViewOnly();
        Assert.True(ps.Has(Permission.ViewScreen));
        Assert.False(ps.Has(Permission.ControlMouse));
        Assert.False(ps.Has(Permission.ControlKeyboard));
        Assert.False(ps.Has(Permission.FileUpload));
    }

    [Fact]
    public void FullControl_HasAllPermissions()
    {
        var ps = PermissionSet.FullControl();
        Assert.True(ps.Has(Permission.ViewScreen));
        Assert.True(ps.Has(Permission.ControlMouse));
        Assert.True(ps.Has(Permission.ControlKeyboard));
        Assert.True(ps.Has(Permission.ClipboardRead));
        Assert.True(ps.Has(Permission.ClipboardWrite));
        Assert.True(ps.Has(Permission.FileUpload));
        Assert.True(ps.Has(Permission.FileDownload));
    }

    [Fact]
    public void Grant_AddsPermission()
    {
        var ps = PermissionSet.ViewOnly();
        ps.Grant(Permission.ControlMouse);
        Assert.True(ps.Has(Permission.ControlMouse));
    }

    [Fact]
    public void Revoke_RemovesPermission()
    {
        var ps = PermissionSet.FullControl();
        ps.Revoke(Permission.RemoteShutdown);
        Assert.False(ps.Has(Permission.RemoteShutdown));
        Assert.True(ps.Has(Permission.ViewScreen));
    }

    [Fact]
    public void DefaultPermission_IsViewOnly_NotFullControl()
    {
        var ps = new PermissionSet();
        Assert.False(ps.Has(Permission.FullControl));
    }
}
