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
$packId = "Frontier15Launcher"
$mainExe = if ($windows) { "Frontier15Launcher.exe" } else { "Frontier15Launcher" }
# The AppImage takes a PNG icon; Setup.exe an .ico.
$icon = if ($windows) { "src/DarkHaven.App/Assets/icon.ico" } else { "src/DarkHaven.App/Assets/emblem.png" }

Write-Host "== Frontier 15 Launcher $Version ($Rid, channel '$Channel') ==" -ForegroundColor Cyan

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pub, $OutputDir | Out-Null

# The launch-proof signing key (DH_LAUNCH_SIGNING_KEY, a CI secret; docs/RELEASING.md) is for the guard's build only
# (step 3): out of the environment until then, and out again after.
$launchKey = $env:DH_LAUNCH_SIGNING_KEY
Remove-Item Env:DH_LAUNCH_SIGNING_KEY -ErrorAction SilentlyContinue

# The loader directory's contents as pins for dh_guard: "<sha256> <path>" per regular file, '/' separators, in a
# stable order. A release guard starts the loader only from a directory that holds exactly these files
# (native/dh-guard, docs/GUARD.md), so this runs on the exact output that ships.
function Get-LoaderPins([string]$dir) {
    $links = @(Get-ChildItem -LiteralPath $dir -Recurse -Force | Where-Object { $_.LinkType })
    if ($links) { throw "the loader output holds links, which dh_guard refuses: $($links.FullName -join ', ')" }
    $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force)
    $paths = [string[]]@($files | ForEach-Object { [System.IO.Path]::GetRelativePath($dir, $_.FullName).Replace('\', '/') })
    $lines = [string[]]@(for ($i = 0; $i -lt $files.Count; $i++) {
        "$((Get-FileHash -LiteralPath $files[$i].FullName -Algorithm SHA256).Hash.ToLowerInvariant()) $($paths[$i])"
    })
    [Array]::Sort($paths, $lines, [StringComparer]::Ordinal)
    return ,$lines
}

# The same pins for the loader inside a built full package (step 6). On Windows the package's lib/app/ is the app
# directory itself. On Linux lib/app/ holds the AppImage, and loader/ is in its squashfs, under usr/bin/.
function Get-ShippedLoaderPins([string]$nupkg) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        if ($windows) {
            $prefix = "lib/app/loader/"
            $entries = @($zip.Entries | Where-Object { $_.FullName.StartsWith($prefix, [StringComparison]::Ordinal) -and -not $_.FullName.EndsWith("/") })
            $paths = [string[]]@($entries | ForEach-Object { $_.FullName.Substring($prefix.Length) })
            $lines = [string[]]@(for ($i = 0; $i -lt $entries.Count; $i++) { "$(Get-EntrySha256 $entries[$i]) $($paths[$i])" })
            [Array]::Sort($paths, $lines, [StringComparer]::Ordinal)
            return ,$lines
        }

        if (-not (Get-Command unsquashfs -ErrorAction SilentlyContinue)) {
            throw "unsquashfs is missing (squashfs-tools): step 6 reads loader/ back out of the AppImage with it"
        }
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "dh-shipped-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            $appImage = Join-Path $tmp "$packId.AppImage"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile((Get-AppImageEntry $zip), $appImage)
            unsquashfs -no-progress -no-xattrs -offset (Get-SquashfsOffset $appImage) -dest (Join-Path $tmp "root") $appImage usr/bin/loader | Out-Null
            if ($LASTEXITCODE) { throw "unsquashfs could not read loader/ out of the AppImage in $nupkg" }
            $shipped = Join-Path $tmp "root/usr/bin/loader"
            if (-not (Test-Path -LiteralPath $shipped)) { throw "the AppImage in $nupkg holds no usr/bin/loader/" }
            return ,(Get-LoaderPins $shipped)
        } finally {
            Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    } finally {
        $zip.Dispose()
    }
}

function Get-AppImageEntry($zip) {
    $entry = $zip.GetEntry("lib/app/$packId.AppImage")
    if (-not $entry) { throw "the full package holds no lib/app/$packId.AppImage" }
    return $entry
}

function Get-EntrySha256($entry) {
    $stream = $entry.Open()
    try { return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

# Where an AppImage's squashfs starts: right after its ELF runtime, whose end is that of its section header table
# (what the runtime's own --appimage-offset reports). 64-bit little-endian runtimes only (x64, arm64).
function Get-SquashfsOffset([string]$appImage) {
    $file = [System.IO.File]::OpenRead($appImage)
    try {
        $header = [byte[]]::new(64)
        if ($file.Read($header, 0, 64) -ne 64 -or $header[0] -ne 0x7f -or $header[1] -ne 0x45 -or $header[4] -ne 2 -or $header[5] -ne 1) {
            throw "$appImage does not start with a 64-bit little-endian ELF runtime"
        }
        $offset = [BitConverter]::ToInt64($header, 0x28) + [BitConverter]::ToUInt16($header, 0x3A) * [BitConverter]::ToUInt16($header, 0x3C)
        $magic = [byte[]]::new(4)
        $file.Position = $offset
        if ($file.Read($magic, 0, 4) -ne 4 -or [System.Text.Encoding]::ASCII.GetString($magic) -ne "hsqs") {
            throw "$appImage has no squashfs at offset $offset"
        }
        return $offset
    } finally {
        $file.Dispose()
    }
}

# 1. The in-process engine loader, into ./loader/ (GameLauncher looks for it there). Self-contained: it is
#    launched as its own process. It goes first: the launcher's guard is built with its pins (step 3).
$loaderDir = Join-Path $pub "loader"
Write-Host "-- publish DarkHaven.Loader into loader/" -ForegroundColor DarkCyan
dotnet publish (Join-Path $repo "src/DarkHaven.Loader/DarkHaven.Loader.csproj") `
    -c Release -r $Rid --self-contained true --disable-build-servers `
    -p:LauncherVersion=$Version -p:DebugType=none -p:DebugSymbols=false `
    -o $loaderDir
if ($LASTEXITCODE) { throw "loader publish failed" }

# 1b. Check: Robust.LoaderApi is the ABI shared with the engine, and Robust.Client binds to exactly
#     1.0.0.0. 0.2.2 and 0.2.3 shipped it restamped to the launcher version (a bare -p:Version flows
#     into the submodule) and the game could not start for anyone. Never again: fail the build.
$loaderApi = Join-Path $loaderDir "Robust.LoaderApi.dll"
if (-not (Test-Path $loaderApi)) { throw "loader/Robust.LoaderApi.dll is missing from the publish output" }
$loaderApiVersion = [System.Reflection.AssemblyName]::GetAssemblyName($loaderApi).Version
if ($loaderApiVersion -ne [version]"1.0.0.0") {
    throw "Robust.LoaderApi is $loaderApiVersion, the engine needs 1.0.0.0 - something passed a bare -p:Version again (use -p:LauncherVersion)"
}
Write-Host "-- Robust.LoaderApi $loaderApiVersion OK" -ForegroundColor DarkGreen

# 1c. vpk leaves files out of every package it builds (1.2.0: createdump*, *.vshost.* and *.nupkg always, and *.pdb
#     through its --exclude default; matched against the whole path), and a keyed guard refuses a loader directory
#     with a pinned file missing. So loader/ holds only what ships before it is pinned: no symbols (DebugType=none
#     above), and not the runtime's createdump. Step 6 checks the packages themselves.
foreach ($file in @(Get-ChildItem -LiteralPath $loaderDir -Recurse -File -Force)) {
    $path = "/loader/" + [System.IO.Path]::GetRelativePath($loaderDir, $file.FullName).Replace('\', '/')
    if ($path -match '/createdump|\.vshost\.|\.nupkg$' -or $path -cmatch '\.pdb') {
        Write-Host "-- dropping $($path.Substring(1)): vpk leaves it out of the packages" -ForegroundColor DarkGray
        Remove-Item -LiteralPath $file.FullName -Force
    }
}

# 2. Pin the loader for dh_guard.
$pinsFile = Join-Path $repo "artifacts/loader-pins.txt"
$pins = Get-LoaderPins $loaderDir
[System.IO.File]::WriteAllText($pinsFile, ($pins -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "-- pinned $($pins.Count) loader files in $pinsFile" -ForegroundColor DarkGreen

# 3. The Avalonia app, self-contained so players need no .NET runtime installed, with the native guard (dh_guard,
#    built by cargo: see src/DarkHaven.Launcher/DhGuard.targets). The signing key reaches the guard's build only
#    through the environment, never a command line. Without it the guard signs nothing.
$keyed = [bool]$launchKey
if (-not $keyed) {
    Write-Warning "DH_LAUNCH_SIGNING_KEY is not set - this build will not sign launch proofs, and Frontier 15 servers enforcing the launcher check will turn its players away."
}
# A keyed cargo target directory holds derivatives of the key (cargo even records the variable's value, in files its
# umask leaves readable to everyone): a fresh owner-only one per build, deleted afterwards. %TEMP% is the user's own on
# Windows; /tmp is shared, so there it is made 0700 before cargo creates anything in it. No build servers either, so
# no idle process keeps the key in its environment.
$cargoDir = if ($keyed) { Join-Path ([System.IO.Path]::GetTempPath()) "dh-guard-$([guid]::NewGuid().ToString('N'))" } else { Join-Path $repo "artifacts/cargo" }
try {
    if ($keyed -and $IsWindows) {
        New-Item -ItemType Directory -Path $cargoDir | Out-Null
    } elseif ($keyed) {
        $ownerOnly = [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute'
        [System.IO.Directory]::CreateDirectory($cargoDir, $ownerOnly) | Out-Null
        if ((Get-Item -LiteralPath $cargoDir).UnixFileMode -ne $ownerOnly) { throw "could not make $cargoDir owner-only" }
    }
    $env:DH_GUARD_LOADER_PINS = $pinsFile
    if ($keyed) { $env:DH_LAUNCH_SIGNING_KEY = $launchKey }

    Write-Host "-- publish DarkHaven.App" -ForegroundColor DarkCyan
    dotnet publish (Join-Path $repo "src/DarkHaven.App/DarkHaven.App.csproj") `
        -c Release -r $Rid --self-contained true --disable-build-servers `
        -p:LauncherVersion=$Version -p:PublishSingleFile=false `
        -p:DhGuardRid=$Rid "-p:DhGuardCargoTargetDir=$cargoDir" `
        -o $pub
    if ($LASTEXITCODE) { throw "app publish failed" }
} finally {
    Remove-Item Env:DH_LAUNCH_SIGNING_KEY, Env:DH_GUARD_LOADER_PINS -ErrorAction SilentlyContinue
    $launchKey = $null
    if ($keyed -and (Test-Path $cargoDir)) { Remove-Item $cargoDir -Recurse -Force }
}

# 3b. The guard is in, and the loader is still exactly what it pins: the app's publish must not touch loader/.
$guardLib = Join-Path $pub $(if ($windows) { "dh_guard.dll" } else { "libdh_guard.so" })
if (-not (Test-Path $guardLib)) { throw "$guardLib is missing from the publish output" }
$after = Get-LoaderPins $loaderDir
if (Compare-Object -CaseSensitive $pins $after) {
    throw "publishing the app changed loader/ after it was pinned: $((Compare-Object -CaseSensitive $pins $after | ForEach-Object InputObject) -join '; ')"
}
Write-Host "-- dh_guard in place ($(if ($keyed) { 'signs' } else { 'development, signs nothing' })), loader/ unchanged since pinned" -ForegroundColor DarkGreen

# 4. Sanity: the forked engine must be bundled or nobody can connect to Frontier 15.
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

# 5. Velopack: installer + delta + release manifest.
Write-Host "-- vpk pack" -ForegroundColor DarkCyan
dotnet tool restore | Out-Null
dotnet vpk pack `
    --packId $packId `
    --packVersion $Version `
    --packDir $pub `
    --mainExe $mainExe `
    --packTitle "Frontier 15 Launcher" `
    --packAuthors "Frontier 15" `
    --icon (Join-Path $repo $icon) `
    --channel $Channel `
    --outputDir $OutputDir
if ($LASTEXITCODE) { throw "vpk pack failed" }

# 6. What ships is what the guard pins. vpk builds every package (full, delta, Setup.exe, the AppImage) from its own
#    copy of the publish output and leaves files out of it on its own (1c), and would sign loader/'s executables if
#    it were given signing options: a loader that differs from its pins is refused on every player's machine. So read
#    loader/ back out of the full package and compare. On Linux the AppImage players download is the one in it.
Add-Type -AssemblyName System.IO.Compression.ZipFile
$assets = @(Get-Content (Join-Path $OutputDir "assets.$Channel.json") -Raw | ConvertFrom-Json)
function Get-Asset([string]$type, [string]$number) {
    $found = @($assets | Where-Object { "$($_.Type)" -in $type, $number })
    if ($found.Count -ne 1) { throw "vpk listed $($found.Count) $type assets in assets.$Channel.json" }
    return Join-Path $OutputDir $found[0].RelativeFileName
}
$fullPkg = Get-Asset "Full" "1"
if (-not $windows) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($fullPkg)
    try { $inPackage = Get-EntrySha256 (Get-AppImageEntry $zip) } finally { $zip.Dispose() }
    $appImage = Get-Asset "Portable" "3"
    if ((Get-FileHash -LiteralPath $appImage -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inPackage) {
        throw "$(Split-Path -Leaf $appImage) is not the AppImage in $(Split-Path -Leaf $fullPkg)"
    }
}
$shipped = Get-ShippedLoaderPins $fullPkg
if (Compare-Object -CaseSensitive $pins $shipped) {
    throw "the loader in $(Split-Path -Leaf $fullPkg) is not what dh_guard pins (<= pinned only, => shipped only): $((Compare-Object -CaseSensitive $pins $shipped | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join '; ')"
}
Write-Host "-- $(Split-Path -Leaf $fullPkg): its $($shipped.Count) loader files are exactly the pinned ones" -ForegroundColor DarkGreen

Write-Host "== done: $OutputDir ==" -ForegroundColor Green
Get-ChildItem $OutputDir | Format-Table Name, Length -AutoSize
