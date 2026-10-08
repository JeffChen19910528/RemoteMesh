<#
.SYNOPSIS
    Builds, tests and packages RemoteMesh for distribution.

.DESCRIPTION
    For each runtime: publishes the server and the agent, adds the deployment files for that platform, writes a
    VERSION file and produces an archive. Finally writes SHA256SUMS.txt for everything in the output folder.

    The default is framework-dependent (needs the .NET 10 ASP.NET Core runtime on the target). Use
    -SelfContained to bundle the runtime instead (larger, no runtime needed on the target).

.EXAMPLE
    ./scripts/publish.ps1                       # tests, then win-x64 + linux-x64 packages in ./artifacts
    ./scripts/publish.ps1 -Runtime win-x64 -SelfContained -SkipTests
#>
[CmdletBinding()]
param(
    [string[]] $Runtime = @('win-x64', 'linux-x64'),
    [switch] $SelfContained,
    [switch] $SkipTests,
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

# Single source of truth for the version: Directory.Build.props
[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
Write-Host "RemoteMesh $version" -ForegroundColor Cyan

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output | Out-Null

Push-Location $root
try {
    # Never package something that does not build and pass its tests.
    Invoke-Dotnet @('build', 'RemoteDesktop.slnx', '-c', 'Release', '--nologo')
    if (-not $SkipTests) { Invoke-Dotnet @('test', 'RemoteDesktop.slnx', '-c', 'Release', '--no-build', '--nologo') }

    foreach ($rid in $Runtime) {
        $name = "remotemesh-$version-$rid"
        $stage = Join-Path $Output $name
        $sc = if ($SelfContained) { 'true' } else { 'false' }

        foreach ($app in @(
                @{ Name = 'server'; Project = 'src/server/RemoteDesktop.Server.Api/RemoteDesktop.Server.Api.csproj' },
                @{ Name = 'agent';  Project = 'src/RemoteDesktop.Agent/RemoteDesktop.Agent.csproj' })) {
            Invoke-Dotnet @('publish', $app.Project, '-c', 'Release', '-r', $rid, "--self-contained:$sc",
                '-o', (Join-Path $stage $app.Name), '--nologo')
        }

        # Deployment files for the platform.
        $deploy = Join-Path $stage 'deploy'
        New-Item -ItemType Directory -Path $deploy | Out-Null
        if ($rid -like 'win-*') {
            Copy-Item (Join-Path $root 'deploy/install-server.ps1') $deploy
        } else {
            Copy-Item (Join-Path $root 'deploy/install-server.sh') $deploy
            Copy-Item (Join-Path $root 'deploy/remotemesh-server.service') $deploy
        }
        Copy-Item (Join-Path $root 'deploy/appsettings.Production.example.json') $deploy
        Copy-Item (Join-Path $root 'deploy/nginx.conf.example') $deploy
        Copy-Item (Join-Path $root 'docs') (Join-Path $stage 'docs') -Recurse
        Copy-Item (Join-Path $root 'README.md') $stage
        Copy-Item (Join-Path $root 'CHANGELOG.md') $stage
        Set-Content -Path (Join-Path $stage 'VERSION') -Value $version -NoNewline

        if ($rid -like 'win-*') {
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $Output "$name.zip")
        } else {
            tar -C $Output -czf (Join-Path $Output "$name.tar.gz") $name
            if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
        }
        Write-Host "Packaged $name" -ForegroundColor Green
    }

    $sums = Get-ChildItem $Output -File | Where-Object { $_.Extension -in '.zip', '.gz' } | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
    }
    Set-Content -Path (Join-Path $Output 'SHA256SUMS.txt') -Value $sums
    Write-Host "Done. Output: $Output" -ForegroundColor Cyan
}
finally { Pop-Location }
