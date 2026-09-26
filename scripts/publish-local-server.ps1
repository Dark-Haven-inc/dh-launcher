# Publishes a server build for ЛОКАЛКА: the launcher's local servers (src/DarkHaven.Launcher/Local).
#
# The build must be a LOCAL-SERVER build of dh-sector-frontier — made with the server-side anti-cheat left out,
# since it goes to every player who opens ЛОКАЛКА — and self-contained, with the client content inside
# (hybrid ACZ), so the local server hands the game its own content and needs no CDN. From a dh-sector-frontier
# checkout (Windows, PowerShell):
#
#   $env:DhLocalServer = 'true'
#   dotnet run --project Content.Packaging server --platform win-x64 --hybrid-acz
#   # -> release/SS14.Server_win-x64.zip
#
# Then, from this repo, with gh signed in to an account that can write to Dark-Haven-inc/frontier15-launcher:
#
#   powershell -File scripts/publish-local-server.ps1 -Zip <path>\release\SS14.Server_win-x64.zip -Version <game commit>
#
# It uploads the zip to the "local-servers" pre-release of the public releases repo (a pre-release, so the
# launcher's own updater never looks at it), adds the build to manifest.json there (Robust.Cdn's format, which
# LocalBuildCatalog reads), and keeps the newest -Keep builds, deleting the zips of older ones.
#
# The launcher has to carry the engine this build was made with (src/DarkHaven.App/bundled-engines): a local
# server built on a newer engine than the launcher bundles won't let the game in.

param(
    [Parameter(Mandatory)] [string] $Zip,
    [Parameter(Mandatory)] [string] $Version,
    [string] $Rid = "win-x64",
    [int] $Keep = 3,
    [string] $Repo = "Dark-Haven-inc/frontier15-launcher",
    [string] $Tag = "local-servers"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Zip)) { throw "No such file: $Zip" }
if ($Version -notmatch '^[A-Za-z0-9._-]{1,64}$') { throw "Version should be the game's commit hash: $Version" }

# The zip must be a local-server build: no server-side anti-cheat inside.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $Zip))
try {
    $serverDll = $archive.Entries | Where-Object { $_.FullName -match '(^|/)Content\.Server\.dll$' } | Select-Object -First 1
    if (-not $serverDll) { throw "No Content.Server.dll in $Zip - is this a server build?" }
    $tmpDll = [System.IO.Path]::GetTempFileName()
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($serverDll, $tmpDll, $true)
    $bytes = [System.IO.File]::ReadAllBytes($tmpDll)
    Remove-Item $tmpDll
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($text.Contains("VisibilityFilterSystem") -or $text.Contains("ClientIntegritySystem")) {
        throw "Content.Server.dll carries the anti-cheat. Build it with `$env:DhLocalServer = 'true' (see the top of this script)."
    }
    if (-not ($archive.Entries | Where-Object { $_.FullName -match 'Content\.Client\.zip$' })) {
        throw "No Content.Client.zip inside: package with --hybrid-acz, or the local server has no content to hand the game."
    }
}
finally { $archive.Dispose() }

$short = if ($Version.Length -gt 12) { $Version.Substring(0, 12) } else { $Version }
$asset = "SS14.Server_${Rid}_$short.zip"
$work = Join-Path ([System.IO.Path]::GetTempPath()) "dh-local-publish-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $staged = Join-Path $work $asset
    Copy-Item $Zip $staged
    $sha = (Get-FileHash $staged -Algorithm SHA256).Hash
    $size = (Get-Item $staged).Length

    # The pre-release, made once.
    gh release view $Tag --repo $Repo *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "-- creating the $Tag pre-release" -ForegroundColor DarkCyan
        gh release create $Tag --repo $Repo --prerelease --title "Сборки для локалки" `
            --notes "Серверные сборки Frontier 15 для вкладки ЛОКАЛКА в лаунчере (без серверного античита). Скачивать вручную не нужно: лаунчер делает это сам."
        if ($LASTEXITCODE) { throw "gh release create failed" }
    }

    # The manifest as it is now, or a fresh one. (Works in Windows PowerShell 5.1 as well as pwsh.)
    $manifestPath = Join-Path $work "manifest.json"
    gh release download $Tag --repo $Repo --pattern manifest.json --dir $work 2>$null
    $builds = [ordered]@{}
    if (Test-Path $manifestPath) {
        $parsed = Get-Content $manifestPath -Raw | ConvertFrom-Json
        if ($parsed.builds) { foreach ($b in $parsed.builds.PSObject.Properties) { $builds[$b.Name] = $b.Value } }
    }

    Write-Host "-- uploading $asset ($([math]::Round($size / 1MB)) MB)" -ForegroundColor DarkCyan
    gh release upload $Tag $staged --repo $Repo --clobber
    if ($LASTEXITCODE) { throw "upload failed" }

    $url = "https://github.com/$Repo/releases/download/$Tag/$asset"
    $builds[$Version] = [pscustomobject]@{
        time   = (Get-Date).ToUniversalTime().ToString("o")
        server = [pscustomobject]@{ $Rid = [pscustomobject]@{ url = $url; sha256 = $sha; size = $size } }
    }

    # Keep the newest -Keep builds; the rest go, zips included.
    $ordered = @($builds.GetEnumerator() | Sort-Object { [datetime]$_.Value.time } -Descending)
    foreach ($old in @($ordered | Select-Object -Skip $Keep)) {
        foreach ($server in $old.Value.server.PSObject.Properties) {
            $oldAsset = ($server.Value.url -split '/')[-1]
            Write-Host "-- dropping old build $($old.Key) ($oldAsset)" -ForegroundColor DarkGray
            gh release delete-asset $Tag $oldAsset --repo $Repo --yes 2>$null
        }
        $builds.Remove($old.Key)
    }

    $json = [pscustomobject]@{ builds = [pscustomobject]$builds } | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding $false))
    gh release upload $Tag $manifestPath --repo $Repo --clobber
    if ($LASTEXITCODE) { throw "manifest upload failed" }

    Write-Host "-- published $Version for $Rid; manifest: https://github.com/$Repo/releases/download/$Tag/manifest.json" -ForegroundColor DarkGreen
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
