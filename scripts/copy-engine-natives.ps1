# Copies the RobustToolbox client native libraries next to the built loader so it can run locally.
#
# The engine zip from robust-builds contains only managed assemblies. The ~12 native libs
# (SDL3, OpenAL, freetype, glfw, etc.) are shipped alongside the loader. Until phase 6 wires this
# into the publish, grab them from an existing SS14 install (Steam or standalone launcher).
#
#   ./scripts/copy-engine-natives.ps1 -Config Release
#   ./scripts/copy-engine-natives.ps1 -Source "D:\path\to\bin_x64\loader"

param(
    [string]$Source,
    [ValidateSet('Debug', 'Release')] [string]$Config = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo ("src/DarkHaven.Loader/bin/{0}/net10.0" -f $Config)

if (-not (Test-Path $dest)) {
    throw ("Loader not built at {0}. Run: dotnet build src/DarkHaven.Loader -c {1}" -f $dest, $Config)
}

$natives = @('OpenAL32', 'SDL3', 'e_sqlite3', 'freetype6', 'glfw3', 'libEGL', 'libGLESv2',
             'libfluidsynth-3', 'libsodium', 'swnfd', 'zlib1', 'zstd') | ForEach-Object { "$_.dll" }

if (-not $Source) {
    $roots = @('C:\Program Files (x86)\Steam\steamapps\common',
               'D:\SteamLibrary\steamapps\common',
               'E:\SteamLibrary\steamapps\common')
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        Get-ChildItem $r -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'Space Station 14*' } |
            ForEach-Object {
                $cand = Join-Path $_.FullName 'bin_x64\loader'
                if ((Test-Path $cand) -and (-not $Source)) { $Source = $cand }
            }
    }
}

if (-not $Source -or -not (Test-Path $Source)) {
    throw 'Could not find a source for engine natives. Pass -Source pointing at a dir with SDL3.dll etc.'
}

Write-Host ("Copying engine natives from: {0}" -f $Source) -ForegroundColor Cyan
$missing = @()
foreach ($n in $natives) {
    $src = Join-Path $Source $n
    if (Test-Path $src) { Copy-Item $src $dest -Force; Write-Host ("  {0}" -f $n) }
    else { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Warning ("not found (may be fine for a different engine): {0}" -f ($missing -join ', '))
}
Write-Host ("Done. -> {0}" -f $dest) -ForegroundColor Green
