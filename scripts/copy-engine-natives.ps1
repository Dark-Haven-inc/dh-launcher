<#
.SYNOPSIS
  Copies the RobustToolbox client native libraries next to the built loader so it can run locally.

.DESCRIPTION
  The engine zip from robust-builds contains only managed assemblies. The ~12 native libs
  (SDL3, OpenAL, Skia deps, freetype, glfw, etc.) are shipped alongside the loader. Until phase 6
  wires this into the publish, grab them from an existing SS14 install.

  Source auto-detected in this order:
    1. -Source argument
    2. Steam: <SteamLibrary>\steamapps\common\Space Station 14*\bin_x64\loader
    3. Standalone launcher install dir\loader

.EXAMPLE
  ./scripts/copy-engine-natives.ps1 -Config Release
#>
param(
    [string]$Source,
    [ValidateSet('Debug', 'Release')] [string]$Config = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo "src/DarkHaven.Loader/bin/$Config/net10.0"

if (-not (Test-Path $dest)) {
    throw "Loader not built at $dest — run: dotnet build src/DarkHaven.Loader -c $Config"
}

$natives = 'OpenAL32', 'SDL3', 'e_sqlite3', 'freetype6', 'glfw3', 'libEGL', 'libGLESv2',
           'libfluidsynth-3', 'libsodium', 'swnfd', 'zlib1', 'zstd' | ForEach-Object { "$_.dll" }

if (-not $Source) {
    $candidates = @()
    Get-ChildItem 'C:\Program Files (x86)\Steam\steamapps\common', 'D:\SteamLibrary\steamapps\common', 'E:\SteamLibrary\steamapps\common' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Space Station 14*' } |
        ForEach-Object { $candidates += (Join-Path $_.FullName 'bin_x64\loader') }
    $Source = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Source -or -not (Test-Path $Source)) {
    throw "Could not find a source for engine natives. Pass -Source <dir containing SDL3.dll etc.>"
}

Write-Host "Copying engine natives from: $Source" -ForegroundColor Cyan
$missing = @()
foreach ($n in $natives) {
    $src = Join-Path $Source $n
    if (Test-Path $src) { Copy-Item $src $dest -Force; Write-Host "  $n" }
    else { $missing += $n }
}
if ($missing) { Write-Warning "not found (may be fine for newer/older engines): $($missing -join ', ')" }
Write-Host "Done -> $dest" -ForegroundColor Green
