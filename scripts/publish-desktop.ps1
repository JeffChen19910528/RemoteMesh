<#
.SYNOPSIS
    Packages the cross-platform RemoteMesh desktop application (and the headless agent) for Linux, macOS or Windows.

.DESCRIPTION
    Builds the Avalonia application and the agent as self-contained programs for one runtime and packs them:
      linux-x64 / linux-arm64  ->  remotemesh-<version>-<rid>.tar.gz   (RemoteMesh/ and agent/)
      osx-arm64 / osx-x64      ->  remotemesh-<version>-<rid>.tar.gz   (RemoteMesh.app and agent/)
      win-x64                  ->  remotemesh-<version>-win-x64.zip    (the Windows build is normally the WPF one: publish-client.ps1)
    Archives are written with explicit Unix permissions, so the programs stay executable whichever system made the
    package (a Windows machine can build the Linux and macOS packages). SHA256SUMS-desktop.txt lists the archives.

    The packages are NOT signed or notarized: macOS Gatekeeper will warn on first run until they are.
    Needs the .NET 10 SDK; runs in Windows PowerShell 5.1 or PowerShell 7.

.EXAMPLE
    ./scripts/publish-desktop.ps1 -Runtime linux-x64
    ./scripts/publish-desktop.ps1 -Runtime osx-arm64 -SkipTests
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('linux-x64', 'linux-arm64', 'osx-arm64', 'osx-x64', 'win-x64')]
    [string] $Runtime,
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

[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
Write-Host "RemoteMesh desktop $version for $Runtime" -ForegroundColor Cyan

$name = "remotemesh-$version-$Runtime"
$work = Join-Path $Output ".stage-$Runtime"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null
New-Item -ItemType Directory -Path $Output -Force | Out-Null

$ui = Join-Path $work 'ui'
$agent = Join-Path $work 'agent'
Push-Location $root
try {
    if (-not $SkipTests) {
        Invoke-Dotnet @('build', 'RemoteDesktop.slnx', '-c', 'Release', '--nologo')
        Invoke-Dotnet @('test', 'RemoteDesktop.slnx', '-c', 'Release', '--no-build', '--nologo')
    }
    Invoke-Dotnet @('publish', 'src/RemoteDesktop.UI.Avalonia/RemoteDesktop.UI.Avalonia.csproj', '-c', 'Release', '-r', $Runtime,
        '--self-contained:true', '-o', $ui, '--nologo')
    Invoke-Dotnet @('publish', 'src/RemoteDesktop.Agent/RemoteDesktop.Agent.csproj', '-c', 'Release', '-r', $Runtime,
        '--self-contained:true', '-o', $agent, '--nologo')
}
finally { Pop-Location }

# Debug symbols are not part of the product.
Get-ChildItem $work -Recurse -Filter *.pdb | Remove-Item -Force

# ---- Layout ---------------------------------------------------------------
$pkg = Join-Path $work 'pkg'
New-Item -ItemType Directory -Path $pkg -Force | Out-Null
$isMac = $Runtime.StartsWith('osx')
$exeName = if ($Runtime -eq 'win-x64') { 'RemoteMesh.exe' } else { 'RemoteMesh' }

if ($isMac) {
    $app = Join-Path $pkg 'RemoteMesh.app'
    $macos = Join-Path $app 'Contents/MacOS'
    New-Item -ItemType Directory -Path $macos -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $app 'Contents/Resources') -Force | Out-Null
    Copy-Item (Join-Path $ui '*') $macos -Recurse -Force
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>RemoteMesh</string>
  <key>CFBundleDisplayName</key><string>RemoteMesh</string>
  <key>CFBundleIdentifier</key><string>app.remotemesh.desktop</string>
  <key>CFBundleExecutable</key><string>RemoteMesh</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
"@
    [System.IO.File]::WriteAllText((Join-Path $app 'Contents/Info.plist'), $plist, (New-Object System.Text.UTF8Encoding($false)))
} else {
    Copy-Item $ui (Join-Path $pkg 'RemoteMesh') -Recurse -Force
}
Copy-Item $agent (Join-Path $pkg 'agent') -Recurse -Force

$application = if ($isMac) { 'RemoteMesh.app' } else { "RemoteMesh/$exeName" }
$readme = "RemoteMesh $version ($Runtime)`n`nDesktop application : $application`nHeadless agent      : agent/RemoteDesktop.Agent  (configure with agent/appsettings.json or Agent__* environment variables)`n`nThis build is not signed.`n"
if ($isMac) {
    $readme += @"

macOS
  * First start: right-click RemoteMesh.app -> Open (Gatekeeper warns about unsigned apps), or run
      xattr -dr com.apple.quarantine RemoteMesh.app
  * Allow RemoteMesh under System Settings -> Privacy & Security -> Screen Recording and -> Accessibility,
    otherwise the screen is blank and mouse/keyboard control is ignored. Restart it after allowing.
  * Keyboard: keys are sent as they are named; Windows Ctrl acts as Control, the Windows key as Command.
"@
} elseif ($Runtime.StartsWith('linux')) {
    $readme += @"

Linux
  * Share and control the screen of an X11 session (choose 'on Xorg' at the login screen). Wayland-only sessions
    cannot be captured yet; this computer can still view and control others.
  * Clipboard sharing needs xclip or xsel (X11), or wl-clipboard (Wayland).
  * Fonts: install a CJK font (for example fonts-noto-cjk) to see the Chinese interface text.
"@
}
[System.IO.File]::WriteAllText((Join-Path $pkg 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($false)))

# ---- Archive with explicit permissions ---------------------------------------
# (.tar.gz is written by a small .NET tool: a Windows file system has no executable bit, and the tool sets it.)
$archive = Join-Path $Output ($(if ($Runtime -eq 'win-x64') { "$name.zip" } else { "$name.tar.gz" }))
if (Test-Path $archive) { Remove-Item $archive -Force }

if ($Runtime -eq 'win-x64') {
    Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath $archive
} else {
    Invoke-Dotnet @('run', '--project', (Join-Path $PSScriptRoot 'pack/PackArchive.csproj'), '-c', 'Release', '--', $pkg, $archive, $name)
}

Remove-Item $work -Recurse -Force
$file = [System.IO.Path]::GetFileName($archive)
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$sums = Join-Path $Output 'SHA256SUMS-desktop.txt'
$existing = @()
if (Test-Path $sums) { $existing = @(Get-Content $sums | Where-Object { $_ -notlike "*$file" }) }
Set-Content -Path $sums -Value ($existing + "$hash  $file") -Encoding ascii

$size = [math]::Round((Get-Item $archive).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $archive  ($size MB)" -ForegroundColor Green
Write-Host "SHA256: $hash"
