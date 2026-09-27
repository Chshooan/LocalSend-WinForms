<#
  LocalSend-WinForms installer packager - version-stamped MSI output.

  WHAT IT DOES
  ------------
  Turns a built app directory into an MSI by compiling installer\Product.wixproj
  (WiX Toolset v4) against it, and drops the result into a NEW versioned folder:

      builds\LocalSend-WinForms-1.0.0+20260927-155504\LocalSend-WinForms-1.0.0+20260927-155504.msi

  WHY THIS EXISTS (instance-lock policy)
  --------------------------------------
  Same rule as build.ps1: NEVER delete or overwrite an artifact. A previous MSI
  may be referenced from an Add/Remove Programs entry, or simply be the one a
  user already downloaded. Each packaging run therefore gets a fresh stamp, and
  if that exact stamp already exists a -2 / -3 suffix is appended.

  WHICH APP DIRECTORY IS PACKAGED
  -------------------------------
  -AppDir wins if given. Otherwise the newest directory under builds\ that
  actually contains LocalSend-WinForms.exe is used - ranked by the versioned
  directory name (+yyyyMMdd-HHmmss), NOT by LastWriteTime, because an
  incremental build copies obj\ output and preserves its timestamps.

  Usage:
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -List
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -AppDir ..\builds\LocalSend-WinForms-1.0.0+20260927-155504
#>
[CmdletBinding()]
param(
    [switch]$List,

    # The framework-dependent publish output of the app.
    [string]$AppDir = '',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Layout: <repo>\LocalSendWin\tools\build-installer.ps1
$toolsRoot    = $PSScriptRoot
$projectRoot  = Split-Path -Parent $toolsRoot
$repoRoot     = Split-Path -Parent $projectRoot
# NOTE: PowerShell 5.1's Join-Path has no -AdditionalChildPath.
$wixproj      = Join-Path (Join-Path $repoRoot 'installer') 'Product.wixproj'
$wxs          = Join-Path (Join-Path $repoRoot 'installer') 'Product.wxs'
$csproj       = Join-Path $projectRoot 'LocalSendWin.csproj'
$buildRoot    = Join-Path $repoRoot 'builds'

function Get-StringProperty {
    param([string]$Path, [string]$Pattern, [string]$Label)

    # ${Label} rather than $Label: a following ':' would otherwise be parsed as part
    # of the variable name (PowerShell raises "Invalid variable reference").
    if (-not (Test-Path -LiteralPath $Path)) { throw "Cannot find ${Label}: ${Path}" }
    $m = [regex]::Match((Get-Content -LiteralPath $Path -Raw), $Pattern)
    if (-not $m.Success) { throw "Could not read ${Label} from ${Path}" }
    return $m.Groups[1].Value.Trim()
}

# ---------------------------------------------------------------------------
# List already packaged installers
# ---------------------------------------------------------------------------
if ($List) {
    if (-not (Test-Path -LiteralPath $buildRoot)) {
        Write-Host "No build output yet ($buildRoot)." -ForegroundColor Yellow
        exit 0
    }
    $msis = @(Get-ChildItem -LiteralPath $buildRoot -Filter '*.msi' -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\_obsolete[^\\]*\\' } |
        Sort-Object LastWriteTime -Descending)
    if ($msis.Count -eq 0) {
        Write-Host "No MSI has been packaged yet." -ForegroundColor Yellow
        exit 0
    }
    foreach ($m in $msis) {
        $hash = (Get-FileHash -LiteralPath $m.FullName -Algorithm SHA256).Hash.Substring(0, 16)
        '{0:yyyy-MM-dd HH:mm:ss}  {1,10}  {2}  {3}' -f $m.LastWriteTime, $m.Length, $hash, $m.FullName
    }
    exit 0
}

# ---------------------------------------------------------------------------
# Sanity checks
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $wixproj)) { throw "Cannot find $wixproj" }

$appVersion = Get-StringProperty -Path $csproj -Pattern '<Version>([^<]+)</Version>' -Label 'the app Version'
$wxsVersion = Get-StringProperty -Path $wxs -Pattern 'Version="([^"]+)"' -Label 'the <Package> Version'
if ($appVersion -ne $wxsVersion) {
    throw "Version mismatch: LocalSendWin.csproj says $appVersion but installer\Product.wxs says $wxsVersion. `n" +
          "Ship the two together or the MSI will report a version Add/Remove Programs does not expect."
}

# ---------------------------------------------------------------------------
# Locate the app directory
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($AppDir)) {
    $candidates = @(Get-ChildItem -LiteralPath $buildRoot -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\_obsolete[^\\]*\\' -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'LocalSend-WinForms.exe'))
        } |
        # the default SDK output is not a release artifact
        Where-Object { $_.FullName -notmatch '\\net8\.0-windows\\' })
    if ($candidates.Count -eq 0) {
        throw "No built app directory found under $buildRoot. Run tools\build.ps1 first, or pass -AppDir."
    }
    # The directory name carries the build stamp; sort descending and take the newest.
    $AppDir = ($candidates | Sort-Object { $_.FullName } -Descending)[0].FullName
    Write-Host "App directory (newest): $AppDir" -ForegroundColor DarkGray
}
$AppDir = (Resolve-Path -LiteralPath $AppDir).Path

# The .wxs references these by path; a missing one means a confusing WiX error later.
$required = @(
    'LocalSend-WinForms.exe',
    'LocalSend-WinForms.dll',
    'LocalSend-WinForms.deps.json',
    'LocalSend-WinForms.runtimeconfig.json',
    (Join-Path 'Resources' 'logo.ico')
)
foreach ($f in $required) {
    $full = Join-Path $AppDir $f
    if (-not (Test-Path -LiteralPath $full)) {
        throw "App directory is missing '$f'. Expected the publish output of tools\build.ps1."
    }
}

# ---------------------------------------------------------------------------
# Versioned output directory (never overwrite)
# ---------------------------------------------------------------------------
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$target = Join-Path $buildRoot ("LocalSend-WinForms-$appVersion+$stamp")
$suffix = 1
while (Test-Path -LiteralPath $target) {
    $suffix++
    $target = Join-Path $buildRoot ("LocalSend-WinForms-$appVersion+$stamp-$suffix")
}

Write-Host "Packaging LocalSend-WinForms $appVersion"
Write-Host "  app   : $AppDir"
Write-Host "  output: $target"
Write-Host

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
# WiX's MSBuild SDK (and the dotnet tool that ships it) live under the same
# user-scoped SDK, so make sure that install wins over any PATH copy.
function Get-DotNet {
    if (Test-Path -LiteralPath "$env:USERPROFILE\.dotnet\dotnet.exe") {
        return "$env:USERPROFILE\.dotnet\dotnet.exe"
    }
    return 'dotnet'
}

$env:DOTNET_ROOT = if (Test-Path -LiteralPath "$env:USERPROFILE\.dotnet") { "$env:USERPROFILE\.dotnet" } else { $env:DOTNET_ROOT }
if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) { $env:PATH = "$env:DOTNET_ROOT;$env:PATH" }

# Build into the project's own obj/bin area first, then copy the single .msi out,
# so the versioned directory contains only what is meant to be shipped.
$stage = Join-Path (Split-Path -Parent $wixproj) 'bin'
$stage = Join-Path $stage $Configuration
& (Get-DotNet) build $wixproj -c $Configuration -p:AppDir="$AppDir" -o $stage
if ($LASTEXITCODE -ne 0) {
    Write-Host "Packaging failed (exit $LASTEXITCODE); $target was not created." -ForegroundColor Red
    exit $LASTEXITCODE
}

$sourceMsi = Join-Path $stage 'Product.msi'
if (-not (Test-Path -LiteralPath $sourceMsi)) {
    throw "WiX reported success but $sourceMsi does not exist."
}

# Only after a successful build do we create the output directory and copy.
if (-not (Test-Path -LiteralPath $target)) {
    New-Item -ItemType Directory -Path $target | Out-Null
}
$destMsi = Join-Path $target ("LocalSend-WinForms-$appVersion+$stamp.msi")
Copy-Item -LiteralPath $sourceMsi -Destination $destMsi -Force

$srcHash = (Get-FileHash -LiteralPath $sourceMsi -Algorithm SHA256).Hash
$dstHash = (Get-FileHash -LiteralPath $destMsi  -Algorithm SHA256).Hash
if ($srcHash -ne $dstHash) {
    throw "Copy verification failed: the staged MSI and the packaged MSI differ."
}

$size = (Get-Item -LiteralPath $destMsi).Length / 1MB
Write-Host
Write-Host "Packaged: $destMsi" -ForegroundColor Green
'  {0:N2} MB, SHA256 {1}' -f $size, $dstHash | Write-Host -ForegroundColor DarkGray
Write-Host "  Install with: msiexec /i `"$destMsi`"" -ForegroundColor DarkGray
