<#
.SYNOPSIS
    Parses every PowerShell script in the repository and fails on syntax errors (no execution).
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$failed = $false
Get-ChildItem $root -Recurse -Filter *.ps1 | Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts)\\' } | ForEach-Object {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref] $null, [ref] $errors) | Out-Null
    if ($errors.Count -gt 0) {
        $failed = $true
        Write-Host "FAIL $($_.FullName)" -ForegroundColor Red
        $errors | ForEach-Object { Write-Host "  line $($_.Extent.StartLineNumber): $($_.Message)" }
    } else {
        Write-Host "ok   $($_.FullName.Substring($root.Length + 1))"
    }
}
if ($failed) { exit 1 }
