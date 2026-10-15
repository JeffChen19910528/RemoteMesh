<#
.SYNOPSIS
    Builds RemoteMesh-<version>-x64.msi, a per-user installer (no administrator rights) for the desktop program.

.DESCRIPTION
    Publishes the client (scripts/publish-client.ps1), wraps RemoteMesh.exe in an MSI with WiX (restored as a local
    dotnet tool from .config/dotnet-tools.json), and signs the exe and the MSI when a certificate is given.
    Without a certificate the MSI is unsigned and Windows SmartScreen warns on first run.

.EXAMPLE
    ./scripts/build-installer.ps1 -SelfContained -SkipTests
    ./scripts/build-installer.ps1 -SelfContained -Thumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [switch] $SelfContained,
    [switch] $SkipTests,
    [string] $Thumbprint,
    [string] $PfxPath,
    [string] $Output = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Output = [System.IO.Path]::GetFullPath($Output)
[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$sign = [bool]($Thumbprint -or $PfxPath)

$clientArgs = @{ Output = $Output }
if ($SelfContained) { $clientArgs.SelfContained = $true }
if ($SkipTests) { $clientArgs.SkipTests = $true }
if ($Thumbprint) { $clientArgs.Thumbprint = $Thumbprint }
if ($PfxPath) { $clientArgs.PfxPath = $PfxPath }
& (Join-Path $PSScriptRoot 'publish-client.ps1') @clientArgs

$stage = Join-Path $Output "remotemesh-client-$version-win-x64"
$msi = Join-Path $Output "RemoteMesh-$version-x64.msi"
Push-Location $root
try {
    & dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
    & dotnet tool run wix build (Join-Path $root 'installer/RemoteMesh.wxs') -arch x64 -d "Version=$version" -d "SourceDir=$stage" -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
}
finally { Pop-Location }

if ($sign) {
    $signArgs = @{ Path = @($msi) }
    if ($Thumbprint) { $signArgs.Thumbprint = $Thumbprint }
    if ($PfxPath) { $signArgs.PfxPath = $PfxPath }
    & (Join-Path $PSScriptRoot 'sign.ps1') @signArgs
}

$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$msi.sha256" -Value "$hash  $(Split-Path $msi -Leaf)" -Encoding ascii
$state = 'unsigned'
if ($sign) { $state = 'signed' }
$mb = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $msi ($mb MB, $state)" -ForegroundColor Green
Write-Host "SHA256: $hash"
