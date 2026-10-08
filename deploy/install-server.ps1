<#
.SYNOPSIS
    Installs (or removes) the RemoteMesh server as a Windows service.

.DESCRIPTION
    Copies the published server into the install directory and registers a service that runs under its own
    low-privilege virtual account (NT SERVICE\RemoteMeshServer). Nothing secret is written: TLS certificates are
    taken from the machine certificate store by subject, so no password is stored in a file or the registry.

    Run from an elevated PowerShell. Use -WhatIf to see every step without changing anything.

.PARAMETER SourceDir
    Folder containing the published server (default: the ../server folder next to this script).

.PARAMETER Urls
    Where the server listens, e.g. "https://+:8443". Use https:// in production (plain http is refused unless
    Security__AllowInsecureTransport is set, which is only meant for a TLS-terminating proxy on the same host).

.PARAMETER CertificateSubject
    Subject (CN) of a certificate in LocalMachine\My to use for HTTPS. The service account is granted read access
    to its private key.

.PARAMETER OpenFirewall
    Adds an inbound firewall rule for the port in -Urls.

.EXAMPLE
    ./install-server.ps1 -Urls "https://+:8443" -CertificateSubject "relay.example.com" -OpenFirewall

.EXAMPLE
    ./install-server.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string] $SourceDir = (Join-Path (Join-Path $PSScriptRoot '..') 'server'),
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'RemoteMesh\Server'),
    [string] $ServiceName = 'RemoteMeshServer',
    [string] $Urls = 'https://+:8443',
    [string] $CertificateSubject,
    [switch] $OpenFirewall,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
$account = "NT SERVICE\$ServiceName"

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal] $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated (Administrator) PowerShell.'
    }
}

# -WhatIf must work for anyone; real changes need elevation.
if (-not $WhatIfPreference) { Assert-Admin }

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($Uninstall) {
    if ($existing -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete service')) {
        if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force }
        sc.exe delete $ServiceName | Out-Null
    }
    if ($PSCmdlet.ShouldProcess($InstallDir, 'Remove install directory')) {
        if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    }
    Get-NetFirewallRule -DisplayName "RemoteMesh Server ($ServiceName)" -ErrorAction SilentlyContinue |
        ForEach-Object { if ($PSCmdlet.ShouldProcess($_.DisplayName, 'Remove firewall rule')) { Remove-NetFirewallRule -InputObject $_ } }
    if (-not $WhatIfPreference) { Write-Host 'Uninstalled.' }
    return
}

$exe = Join-Path $InstallDir 'RemoteDesktop.Server.Api.exe'
if (-not (Test-Path (Join-Path $SourceDir 'RemoteDesktop.Server.Api.dll')) -and -not $WhatIfPreference) {
    throw "No published server found in '$SourceDir'. Pass -SourceDir or run scripts/publish.ps1 first."
}

# Upgrade in place: stop the running service first so files are not locked.
if ($existing -and $existing.Status -ne 'Stopped' -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop running service')) {
    Stop-Service $ServiceName -Force
}

if ($PSCmdlet.ShouldProcess($InstallDir, "Copy server files from $SourceDir")) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item (Join-Path $SourceDir '*') $InstallDir -Recurse -Force
    # The service account may read the program, never modify it.
    icacls $InstallDir /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "${account}:(OI)(CI)RX" | Out-Null
}

if (-not $existing -and $PSCmdlet.ShouldProcess($ServiceName, "Create service running as $account")) {
    sc.exe create $ServiceName binPath= "`"$exe`"" start= delayed-auto obj= $account `
        DisplayName= 'RemoteMesh Server' | Out-Null
    sc.exe description $ServiceName 'RemoteMesh signaling and relay server' | Out-Null
    # Restart on failure: 5 s, 30 s, then 60 s; reset the failure count after a day.
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
}

# Service environment (no secrets): listening address and, optionally, the certificate by subject.
$env = @("ASPNETCORE_URLS=$Urls", 'ASPNETCORE_ENVIRONMENT=Production')
if ($CertificateSubject) {
    $env += "Kestrel__Certificates__Default__Subject=$CertificateSubject"
    $env += 'Kestrel__Certificates__Default__Store=My'
    $env += 'Kestrel__Certificates__Default__Location=LocalMachine'
}
if ($PSCmdlet.ShouldProcess("HKLM service '$ServiceName'", "Set environment: $($env -join '; ')")) {
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Value $env -Type MultiString
}

if ($OpenFirewall) {
    $port = if ($Urls -match ':(\d+)') { [int] $Matches[1] } else { 8443 }
    if ($PSCmdlet.ShouldProcess("TCP $port", 'Add inbound firewall rule')) {
        New-NetFirewallRule -DisplayName "RemoteMesh Server ($ServiceName)" -Direction Inbound -Protocol TCP `
            -LocalPort $port -Action Allow | Out-Null
    }
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    Start-Service $ServiceName
    Write-Host "Started. Check:  Invoke-RestMethod ${Urls -replace '\+','localhost'}/health"
}
