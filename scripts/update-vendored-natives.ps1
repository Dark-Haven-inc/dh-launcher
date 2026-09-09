# Refreshes the vendored RobustToolbox client native libraries in
# src/DarkHaven.Loader/natives/win-x64/ from an existing SS14 install.
#
# These are checked into the repo and copied to the loader output automatically (see the
# loader .csproj) — run this only to bump them when the engine's native set changes.
#
#   ./scripts/update-vendored-natives.ps1
#   ./scripts/update-vendored-natives.ps1 -Source "D:\path\to\bin_x64\loader"

param([string]$Source)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo 'src/DarkHaven.Loader/natives/win-x64'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$natives = @('OpenAL32', 'SDL3', 'e_sqlite3', 'freetype6', 'glfw3', 'libEGL', 'libGLESv2',
             'libfluidsynth-3', 'libsodium', 'swnfd', 'zlib1', 'zstd') | ForEach-Object { "$_.dll" }

if (-not $Source) {
    $roots = @('C:\Program Files (x86)\Steam\steamapps\common',
               'D:\SteamLibrary\steamapps\common', 'E:\SteamLibrary\steamapps\common')
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
    throw 'Could not find a source. Pass -Source pointing at a dir containing SDL3.dll etc.'
}

Write-Host ("Refreshing vendored natives from: {0}" -f $Source) -ForegroundColor Cyan
$missing = @()
foreach ($n in $natives) {
    $src = Join-Path $Source $n
    if (Test-Path $src) { Copy-Item $src $dest -Force; Write-Host ("  {0}" -f $n) }
    else { $missing += $n }
}
if ($missing.Count -gt 0) { Write-Warning ("not found: {0}" -f ($missing -join ', ')) }
Write-Host ("Done. -> {0}" -f $dest) -ForegroundColor Green
