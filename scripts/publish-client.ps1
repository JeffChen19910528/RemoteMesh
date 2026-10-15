<#
.SYNOPSIS
    Publishes the RemoteMesh desktop application as a single RemoteMesh.exe (Windows).

.DESCRIPTION
    Builds the WPF client in Release and publishes it as one executable plus SHA256SUMS.txt.

    Default: framework-dependent (small; the computer needs the .NET 10 Desktop Runtime).
    -SelfContained bundles the runtime so the exe runs on any Windows 10/11 x64 machine (much larger).

    The exe is signed when -Thumbprint (certificate store) or -PfxPath is given (see scripts/sign.ps1); without a
    certificate it is not, and Windows SmartScreen warns on first run.

.EXAMPLE
    ./scripts/publish-client.ps1
    ./scripts/publish-client.ps1 -SelfContained -SkipTests
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

function Invoke-Dotnet {
    param([string[]] $Arguments)
    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}

[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
Write-Host "RemoteMesh client $version" -ForegroundColor Cyan

$flavor = 'framework-dependent'
if ($SelfContained) { $flavor = 'self-contained' }
$stage = Join-Path $Output "remotemesh-client-$version-win-x64"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Push-Location $root
try {
    Invoke-Dotnet @('build', 'RemoteDesktop.slnx', '-c', 'Release', '--nologo')
    if (-not $SkipTests) { Invoke-Dotnet @('test', 'RemoteDesktop.slnx', '-c', 'Release', '--no-build', '--nologo') }

    $sc = 'false'
    if ($SelfContained) { $sc = 'true' }
    $publishArgs = @('publish', 'src/RemoteDesktop.UI/RemoteDesktop.UI.csproj', '-c', 'Release', '-r', 'win-x64',
        "--self-contained:$sc", '-p:PublishSingleFile=true', '-o', $stage, '--nologo')
    # Self-contained single-file needs the native WPF libraries inside the exe too.
    if ($SelfContained) { $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true' }
    Invoke-Dotnet $publishArgs
}
finally { Pop-Location }

# Debug symbols are not part of the product.
Get-ChildItem $stage -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $stage 'RemoteMesh.exe'
if (-not (Test-Path $exe)) { throw 'RemoteMesh.exe was not produced.' }

if ($Thumbprint -or $PfxPath) {
    $signArgs = @{ Path = @($exe) }
    if ($Thumbprint) { $signArgs.Thumbprint = $Thumbprint }
    if ($PfxPath) { $signArgs.PfxPath = $PfxPath }
    & (Join-Path $PSScriptRoot 'sign.ps1') @signArgs
}

Set-Content -Path (Join-Path $stage 'VERSION') -Value "$version ($flavor)" -Encoding ascii
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path (Join-Path $stage 'SHA256SUMS.txt') -Value "$hash  RemoteMesh.exe" -Encoding ascii

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $exe  ($size MB, $flavor)" -ForegroundColor Green
Write-Host "SHA256: $hash"
