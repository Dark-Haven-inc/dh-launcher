# Makes a new launch-proof key pair and puts the private half straight into the DH_LAUNCH_SIGNING_KEY secret of the
# release workflow through gh. The private half never touches the disk or the terminal; the public half is printed
# for the game servers' anticheat.launch.public_keys.
#
#   ./scripts/set-launch-key.ps1 [-Repo owner/name] [-Yes]
#   scripts\set-launch-key.cmd  [-Repo owner/name] [-Yes]         # from cmd / Explorer
#
# Needs dotnet and gh (logged in, with admin rights on the repo). Windows PowerShell 5.1 or pwsh 7.
# See docs/RELEASING.md, "Launch-proof signing key". The same for Linux: scripts/set-launch-key.sh.

param(
    [string]$Repo,
    [switch]$Yes
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

foreach ($tool in "gh", "dotnet") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not installed" }
}

# Windows PowerShell turns a native command's stderr into errors, which "Stop" would throw on.
$ErrorActionPreference = "Continue"
gh auth status *> $null
$loggedIn = $LASTEXITCODE -eq 0
$ErrorActionPreference = "Stop"
if (-not $loggedIn) { throw "gh is not logged in: gh auth login" }

if (-not $Repo) {
    $Repo = gh repo view --json nameWithOwner -q .nameWithOwner
    if ($LASTEXITCODE -ne 0 -or -not $Repo) { throw "could not tell the repo; pass -Repo owner/name" }
}

$existing = gh secret list --repo $Repo | ForEach-Object { ($_ -split "`t")[0] }
if ($existing -contains "DH_LAUNCH_SIGNING_KEY") {
    Write-Host "$Repo already has DH_LAUNCH_SIGNING_KEY. Replacing it means the next release signs with a new key;"
    Write-Host "servers must trust the new public key before players get that release."
    if (-not $Yes) {
        $answer = Read-Host "Replace it? [y/N]"
        if ($answer -notin "y", "Y", "yes") { Write-Host "Nothing changed."; exit 1 }
    }
}

Write-Host "Building the CLI..."
# launch-key needs nothing from dh_guard, so no Rust toolchain either.
dotnet build src/DarkHaven.Cli -c Release -v q -nologo -p:DhGuardSkipCargo=true | Out-Null
if ($LASTEXITCODE -ne 0) { throw "the CLI did not build" }

$output = @(dotnet run --no-build --project src/DarkHaven.Cli -c Release -- launch-key)
if ($LASTEXITCODE -ne 0) { throw "launch-key failed" }

function Get-LineAfter([string[]]$lines, [string]$header) {
    for ($i = 0; $i -lt $lines.Count - 1; $i++) {
        if ($lines[$i].StartsWith($header)) { return $lines[$i + 1].Trim() }
    }
    return $null
}

$secret = Get-LineAfter $output "CI secret DH_LAUNCH_SIGNING_KEY"
$publicKey = Get-LineAfter $output "Game server cvar anticheat.launch.public_keys"
$output = $null

if (-not $secret -or -not $publicKey) {
    $secret = $null
    throw "could not read a key pair from 'launch-key' (is this branch older than the split-key signer?)"
}

# Written to gh's stdin exactly, without a trailing newline and without ever being on a command line.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Get-Command gh).Source
$psi.Arguments = "secret set DH_LAUNCH_SIGNING_KEY --repo `"$Repo`""
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$gh = [System.Diagnostics.Process]::Start($psi)
$gh.StandardInput.Write($secret)
$gh.StandardInput.Close()
$gh.WaitForExit()
$secret = $null
if ($gh.ExitCode -ne 0) { throw "gh secret set failed ($($gh.ExitCode))" }

Write-Host ""
Write-Host "DH_LAUNCH_SIGNING_KEY set on $Repo."
Write-Host ""
Write-Host "Append to anticheat.launch.public_keys on every game server (comma-separated, keep the previous key"
Write-Host "until players have updated past the last release signed with it):"
Write-Host $publicKey
