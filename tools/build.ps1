<#
  LocalSendWin build helper - version-stamped output directories.

  WHY THIS EXISTS (instance-lock policy)
  --------------------------------------
  A running LocalSendWin.exe keeps the previous build's .exe / .dll open on
  Windows, so `dotnet build -o <dir>` fails with
      "The process cannot access the file ... because it is being used by another process"
  Common (bad) workarounds are deleting the output directory or overwriting the
  locked file. Both destroy a possibly-still-usable artifact and leave no trace
  of which build a running instance actually loaded.

  This script instead NEVER deletes or overwrites. It writes the build into a
  NEW directory whose name carries the assembly version plus a build timestamp:

      builds\LocalSendWin-1.0.0.0+20260927-134900\
      builds\LocalSendWin-1.0.0.0+20260927-140500-2\

  If the exact stamp already exists a -2 / -3 / ... suffix is appended, so the
  newest artifact is always identifiable from its directory name alone, and the
  locked artifact stays untouched for as long as the instance needs it.

  Usage:
    powershell -ExecutionPolicy Bypass -File tools\build.ps1
    powershell -ExecutionPolicy Bypass -File tools\build.ps1 -List
#>
[CmdletBinding()]
param(
    [switch]$List,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Layout: <repo>\LocalSendWin\tools\build.ps1
#   $PSScriptRoot      -> <repo>\LocalSendWin\tools
#   $projectRoot       -> <repo>\LocalSendWin        (the project directory)
#   $repoRoot          -> <repo>
# $PSScriptRoot is always absolute, unlike Split-Path $MyInvocation.MyCommand.Path
# which stays relative to the caller when invoked with -File.
$projectRoot = Split-Path -Parent $PSScriptRoot
$repoRoot    = Split-Path -Parent $projectRoot
# NOTE: Windows PowerShell 5.1's Join-Path has no -AdditionalChildPath, so nest it.
$project        = Join-Path $projectRoot 'LocalSendWin.csproj'
$buildRoot      = Join-Path $repoRoot 'builds'
# Ad-hoc output directories used before the versioned scheme existed. They are only
# ever READ here, never moved or deleted - an instance may still be running off one.
$legacyRoots = @(
    (Join-Path $repoRoot 'build-verify'),
    (Join-Path $projectRoot 'build-verify'),
    (Join-Path $projectRoot 'bin')
)

if (-not (Test-Path -LiteralPath $project)) {
    throw "Cannot find LocalSendWin.csproj under $repoRoot"
}

function Get-CsProperty {
    param([string]$CsPath, [string]$Name)

    $raw = Get-Content -LiteralPath $CsPath -Raw
    $m = [regex]::Match($raw, "<$Name>([^<]+)</$Name>")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return ''
}

function Get-BuildArtifacts {
    param([string[]]$Roots)

    $found = @()
    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $found += Get-ChildItem -LiteralPath $root -Filter 'LocalSend-WinForms.exe' -Recurse -File |
            # the default SDK output (bin\Release\net8.0-windows) is not a release artifact
            Where-Object { $_.Directory.Name -ne 'net8.0-windows' } |
            # superseded artifacts are archived here, not deleted - keep them off the list
            Where-Object { $_.FullName -notmatch '\\_obsolete[^\\]*\\' }
    }
    return $found | Select-Object FullName, DirectoryName, LastWriteTime, Length
}

# NOTE: do not rely on LastWriteTime alone to rank artifacts. An incremental build
# copies the obj\ output and PRESERVES its timestamp, so a build made just now can
# display an older-looking mtime than the artifact it replaced. The versioned
# directory name (+yyyyMMdd-HHmmss) is the authoritative build stamp.
function Show-Artifacts {
    param([string]$HighlightPrefix)

    # parentheses are required: without them PowerShell parses `+ $legacyRoots` as a
    # separate pipeline element instead of appending it to the array argument
    $items = @(Get-BuildArtifacts -Roots (@($buildRoot) + $legacyRoots))
    if ($items.Count -eq 0) {
        Write-Host "No build artifacts found." -ForegroundColor Yellow
        return
    }

    foreach ($it in ($items | Sort-Object LastWriteTime -Descending)) {
        # The .exe apphost is byte-identical across builds (it just loads the runtime),
        # so hashing it distinguishes nothing. The managed payload lives in LocalSendWin.dll.
        $payload = Join-Path $it.DirectoryName 'LocalSend-WinForms.dll'
        if (-not (Test-Path -LiteralPath $payload)) { $payload = $it.FullName }
        $hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.Substring(0, 16)
        $mark = ' '
        if (-not [string]::IsNullOrWhiteSpace($HighlightPrefix) -and $it.FullName.StartsWith($HighlightPrefix)) {
            $mark = '*'
        }
        '{0} {1:yyyy-MM-dd HH:mm:ss}  {2,10}  {3}  {4}' -f $mark, $it.LastWriteTime, $it.Length, $hash, $it.FullName
    }
    Write-Host "  (* = newest artifact)" -ForegroundColor DarkGray
}

if ($List) {
    Show-Artifacts
    exit 0
}

$version = Get-CsProperty -CsPath $project -Name 'Version'
if ([string]::IsNullOrWhiteSpace($version)) { $version = '0.0.0' }

$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$target = Join-Path $buildRoot ("LocalSend-WinForms-$version+$stamp")
$suffix = 1
while (Test-Path -LiteralPath $target) {
    $suffix++
    $target = Join-Path $buildRoot ("LocalSend-WinForms-$version+$stamp-$suffix")
}

# Informational only: we never kill a running instance, and we never touch its files.
$running = @(Get-Process -Name 'LocalSendWin' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "Note: $($running.Count) LocalSendWin instance(s) are running and still hold"
    Write-Host "      the previous build open. Writing to a new stamped directory so nothing is lost."
    foreach ($p in $running) {
        Write-Host "      pid=$($p.Id) started=$($p.StartTime) path=$($p.Path)"
    }
    Write-Host
}

Write-Host "Building -> $target"
Write-Host

$dotnet = 'dotnet'
if (Test-Path -LiteralPath "$env:USERPROFILE\.dotnet\dotnet.exe") {
    $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
}

& $dotnet build $project -c $Configuration -o $target
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed (exit $LASTEXITCODE); output directory left in place for inspection." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host
Show-Artifacts -HighlightPrefix $target
