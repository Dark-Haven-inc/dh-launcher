# Builds a Frontier 15 Launcher release: a Velopack installer plus a delta package, ready to
# upload to GitHub Releases. Players install once (Setup.exe on Windows, the .AppImage on Linux);
# every later version arrives as a small in-app delta.
#
#   ./scripts/pack-release.ps1 -Version 0.1.0
#   ./scripts/pack-release.ps1 -Version 0.1.0 -Rid linux-x64      # on Linux (pwsh)
#   ./scripts/pack-release.ps1 -Version 0.2.0 -Channel beta
#
# CI does this on a v* tag (see .github/workflows/release.yml).

param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [string]$Rid = "win-x64",
    # Velopack's own default per OS: "win" / "linux".
    [string]$Channel,
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$pub  = Join-Path $repo "artifacts/publish"
if (-not $OutputDir) { $OutputDir = Join-Path $repo "artifacts/releases" }

$windows = $Rid.StartsWith("win-")
if (-not $windows -and -not $Rid.StartsWith("linux-")) { throw "unsupported RID $Rid (win-* or linux-*)" }
if (-not $Channel) { $Channel = if ($windows) { "win" } else { "linux" } }
$mainExe = if ($windows) { "Frontier15Launcher.exe" } else { "Frontier15Launcher" }
# The AppImage takes a PNG icon; Setup.exe an .ico.
$icon = if ($windows) { "src/DarkHaven.App/Assets/icon.ico" } else { "src/DarkHaven.App/Assets/emblem.png" }

Write-Host "== Frontier 15 Launcher $Version ($Rid, channel '$Channel') ==" -ForegroundColor Cyan

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pub, $OutputDir | Out-Null

# 1. The Avalonia app, self-contained so players need no .NET runtime installed.
Write-Host "-- publish DarkHaven.App" -ForegroundColor DarkCyan
dotnet publish (Join-Path $repo "src/DarkHaven.App/DarkHaven.App.csproj") `
    -c Release -r $Rid --self-contained true `
    -p:LauncherVersion=$Version -p:PublishSingleFile=false `
    -o $pub
if ($LASTEXITCODE) { throw "app publish failed" }

# 2. The in-process engine loader, into ./loader/ (GameLauncher looks for it there).
#    Also self-contained: it is launched as its own process.
Write-Host "-- publish DarkHaven.Loader into loader/" -ForegroundColor DarkCyan
dotnet publish (Join-Path $repo "src/DarkHaven.Loader/DarkHaven.Loader.csproj") `
    -c Release -r $Rid --self-contained true `
    -p:LauncherVersion=$Version `
    -o (Join-Path $pub "loader")
if ($LASTEXITCODE) { throw "loader publish failed" }

# 2b. Guard: Robust.LoaderApi is the ABI shared with the engine, and Robust.Client binds to exactly
#     1.0.0.0. 0.2.2 and 0.2.3 shipped it restamped to the launcher version (a bare -p:Version flows
#     into the submodule) and the game could not start for anyone. Never again: fail the build.
$loaderApi = Join-Path $pub "loader/Robust.LoaderApi.dll"
if (-not (Test-Path $loaderApi)) { throw "loader/Robust.LoaderApi.dll is missing from the publish output" }
$loaderApiVersion = [System.Reflection.AssemblyName]::GetAssemblyName($loaderApi).Version
if ($loaderApiVersion -ne [version]"1.0.0.0") {
    throw "Robust.LoaderApi is $loaderApiVersion, the engine needs 1.0.0.0 - something passed a bare -p:Version again (use -p:LauncherVersion)"
}
Write-Host "-- Robust.LoaderApi $loaderApiVersion OK" -ForegroundColor DarkGreen

# 3. Sanity: the forked engine must be bundled or nobody can connect to Frontier 15.
#    Every version in manifest.json must have a build for this RID, present with a matching SHA-256:
#    the top-level file is the win-x64 build, "platforms" holds the others by RID. The other RIDs'
#    zips are dropped from this package.
$bundledDir = Join-Path $pub "bundled-engines"
$manifestFile = Join-Path $bundledDir "manifest.json"
if (-not (Test-Path $manifestFile)) {
    Write-Warning "no bundled-engines/manifest.json in the publish output - the launcher will fall back to the public CDN and cannot connect to Frontier 15."
} else {
    $manifest = Get-Content $manifestFile -Raw | ConvertFrom-Json
    $keep = @()
    foreach ($ver in $manifest.PSObject.Properties) {
        $build = $null
        if ($ver.Value.platforms -and $ver.Value.platforms.PSObject.Properties[$Rid]) {
            $build = $ver.Value.platforms.PSObject.Properties[$Rid].Value
        } elseif ($Rid -eq "win-x64" -and $ver.Value.file) {
            $build = $ver.Value
        }
        if (-not $build) {
            throw "bundled engine $($ver.Name) has no $Rid build in manifest.json - see src/DarkHaven.App/bundled-engines/README.md"
        }

        $file = Join-Path $bundledDir $build.file
        if (-not (Test-Path $file)) {
            throw "bundled engine $($ver.Name): $($build.file) is missing. Fetch it (gh release download engine-bundles) or see src/DarkHaven.App/bundled-engines/README.md"
        }
        $sha = (Get-FileHash $file -Algorithm SHA256).Hash
        if ($sha -ne $build.sha256) {
            throw "bundled engine $($ver.Name): SHA-256 mismatch (manifest $($build.sha256), file $sha)"
        }
        $keep += $build.file
        Write-Host "-- bundled engine $($ver.Name) ($Rid): $($build.file) OK ($sha)" -ForegroundColor DarkGreen
    }
    Get-ChildItem $bundledDir -Filter *.zip | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {
        Write-Host "-- dropping $($_.Name): not a $Rid build" -ForegroundColor DarkGray
        Remove-Item $_.FullName
    }
}

# 4. Velopack: installer + delta + release manifest.
Write-Host "-- vpk pack" -ForegroundColor DarkCyan
dotnet tool restore | Out-Null
dotnet vpk pack `
    --packId Frontier15Launcher `
    --packVersion $Version `
    --packDir $pub `
    --mainExe $mainExe `
    --packTitle "Frontier 15 Launcher" `
    --packAuthors "Frontier 15" `
    --icon (Join-Path $repo $icon) `
    --channel $Channel `
    --outputDir $OutputDir
if ($LASTEXITCODE) { throw "vpk pack failed" }

Write-Host "== done: $OutputDir ==" -ForegroundColor Green
Get-ChildItem $OutputDir | Format-Table Name, Length -AutoSize
