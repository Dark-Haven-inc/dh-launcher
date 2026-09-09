# Builds a Dark Haven Launcher release: a Velopack installer (Setup.exe) plus a delta package,
# ready to upload to GitHub Releases. Players run Setup.exe once; every later version arrives as
# a small in-app delta.
#
#   ./scripts/pack-release.ps1 -Version 0.1.0
#   ./scripts/pack-release.ps1 -Version 0.2.0 -Channel beta
#
# CI does this on a v* tag (see .github/workflows/release.yml).

param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [string]$Rid = "win-x64",
    [string]$Channel = "win",
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$pub  = Join-Path $repo "artifacts/publish"
if (-not $OutputDir) { $OutputDir = Join-Path $repo "artifacts/releases" }

Write-Host "== Dark Haven Launcher $Version ($Rid, channel '$Channel') ==" -ForegroundColor Cyan

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pub, $OutputDir | Out-Null

# 1. The Avalonia app, self-contained so players need no .NET runtime installed.
Write-Host "-- publish DarkHaven.App" -ForegroundColor DarkCyan
dotnet publish (Join-Path $repo "src/DarkHaven.App/DarkHaven.App.csproj") `
    -c Release -r $Rid --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=false `
    -o $pub
if ($LASTEXITCODE) { throw "app publish failed" }

# 2. The in-process engine loader, into ./loader/ (GameLauncher looks for it there).
#    Also self-contained: it is launched as its own process.
Write-Host "-- publish DarkHaven.Loader into loader/" -ForegroundColor DarkCyan
dotnet publish (Join-Path $repo "src/DarkHaven.Loader/DarkHaven.Loader.csproj") `
    -c Release -r $Rid --self-contained true `
    -p:Version=$Version `
    -o (Join-Path $pub "loader")
if ($LASTEXITCODE) { throw "loader publish failed" }

# 3. Sanity: the forked engine must be bundled or nobody can connect to Dark Haven.
$engineZip = Get-ChildItem (Join-Path $pub "bundled-engines") -Filter *.zip -ErrorAction SilentlyContinue
if (-not $engineZip) {
    Write-Warning "no bundled engine zip in artifacts/publish/bundled-engines/. Build it first: see src/DarkHaven.App/bundled-engines/README.md"
}

# 4. Velopack: installer + delta + release manifest.
Write-Host "-- vpk pack" -ForegroundColor DarkCyan
dotnet tool restore | Out-Null
dotnet vpk pack `
    --packId DarkHavenLauncher `
    --packVersion $Version `
    --packDir $pub `
    --mainExe DarkHavenLauncher.exe `
    --packTitle "Dark Haven Launcher" `
    --packAuthors "Dark Haven" `
    --icon (Join-Path $repo "src/DarkHaven.App/Assets/icon.ico") `
    --channel $Channel `
    --outputDir $OutputDir
if ($LASTEXITCODE) { throw "vpk pack failed" }

Write-Host "== done: $OutputDir ==" -ForegroundColor Green
Get-ChildItem $OutputDir | Format-Table Name, Length -AutoSize
