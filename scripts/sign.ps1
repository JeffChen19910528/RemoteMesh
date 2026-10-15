<#
.SYNOPSIS
    Authenticode-signs files (exe, msi, dll) with signtool and a timestamp.

.DESCRIPTION
    Give the certificate either as a thumbprint in the current user's certificate store (-Thumbprint, for example a
    hardware token or a cloud HSM that exposes a store entry) or as a PFX file (-PfxPath, with the password in the
    environment variable named by -PfxPasswordVariable, never on the command line).
    The certificate itself is not part of this repository: it has to come from a certificate authority.

.EXAMPLE
    ./scripts/sign.ps1 -Path artifacts/remotemesh-client-0.1.0-win-x64/RemoteMesh.exe -Thumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Path,
    [string] $Thumbprint,
    [string] $PfxPath,
    [string] $PfxPasswordVariable = 'REMOTEMESH_PFX_PASSWORD',
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
if (-not $Thumbprint -and -not $PfxPath) { throw 'Give -Thumbprint or -PfxPath.' }

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin") |
        Where-Object { $_ -and (Test-Path $_) }
    foreach ($r in $roots) {
        $hit = Get-ChildItem $r -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw 'signtool.exe not found (install the Windows SDK, or put signtool.exe on the PATH).'
}

$signtool = Find-SignTool
foreach ($file in $Path) {
    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($Thumbprint) { $signArgs += @('/sha1', $Thumbprint) }
    else {
        $signArgs += @('/f', $PfxPath)
        $password = [Environment]::GetEnvironmentVariable($PfxPasswordVariable)
        if ($password) { $signArgs += @('/p', $password) }
    }
    $signArgs += $file
    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
    & $signtool verify /pa $file | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The signature on $file did not verify." }
    Write-Host "Signed: $file" -ForegroundColor Green
}
