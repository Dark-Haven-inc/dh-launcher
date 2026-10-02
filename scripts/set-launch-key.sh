#!/usr/bin/env bash
# Makes a new launch-proof key pair and puts the private half straight into the DH_LAUNCH_SIGNING_KEY secret of the
# release workflow through gh. The private half never touches the disk or the terminal; the public half is printed
# for the game servers' anticheat.launch.public_keys.
#
#   scripts/set-launch-key.sh [--repo owner/name] [--yes]
#
# Needs dotnet and gh (logged in, with admin rights on the repo). See docs/RELEASING.md, "Launch-proof signing key".
set -euo pipefail

repo=""
assume_yes=0
while [ $# -gt 0 ]; do
    case "$1" in
        --repo) repo="$2"; shift 2 ;;
        --yes|-y) assume_yes=1; shift ;;
        -h|--help) sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

command -v gh >/dev/null || { echo "gh is not installed" >&2; exit 1; }
command -v dotnet >/dev/null || { echo "dotnet is not installed" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "gh is not logged in: gh auth login" >&2; exit 1; }

[ -n "$repo" ] || repo="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

if gh secret list --repo "$repo" | cut -f1 | grep -qx DH_LAUNCH_SIGNING_KEY; then
    echo "$repo already has DH_LAUNCH_SIGNING_KEY. Replacing it means the next release signs with a new key;"
    echo "servers must trust the new public key before players get that release."
    if [ "$assume_yes" -ne 1 ]; then
        read -r -p "Replace it? [y/N] " answer
        case "$answer" in y|Y|yes|да) ;; *) echo "Nothing changed."; exit 1 ;; esac
    fi
fi

echo "Building the CLI..."
# launch-key needs nothing from dh_guard, so no Rust toolchain either.
dotnet build src/DarkHaven.Cli -c Release -v q -nologo -p:DhGuardSkipCargo=true >/dev/null

output="$(dotnet run --no-build --project src/DarkHaven.Cli -c Release -- launch-key)"
secret="$(printf '%s\n' "$output" | awk '/^CI secret DH_LAUNCH_SIGNING_KEY/ { getline; print; exit }')"
public_key="$(printf '%s\n' "$output" | awk '/^Game server cvar anticheat.launch.public_keys/ { getline; print; exit }')"
unset output

if [ -z "$secret" ] || [ -z "$public_key" ]; then
    unset secret
    echo "Could not read a key pair from 'launch-key' (is this branch older than the split-key signer?)" >&2
    exit 1
fi

# printf is a shell builtin, so the secret never shows up in a process list.
printf '%s' "$secret" | gh secret set DH_LAUNCH_SIGNING_KEY --repo "$repo"
unset secret

echo
echo "DH_LAUNCH_SIGNING_KEY set on $repo."
echo
echo "Append to anticheat.launch.public_keys on every game server (comma-separated, keep the previous key"
echo "until players have updated past the last release signed with it):"
echo "$public_key"
