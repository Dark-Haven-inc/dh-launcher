#!/bin/sh
# Bumps the AUR package to a released version: pkgver, checksums, .SRCINFO. Needs makepkg
# (Arch, or an archlinux container). Then commit PKGBUILD + .SRCINFO to the AUR repo
# (ssh://aur@aur.archlinux.org/frontier15-launcher-bin.git).
#
#   ./update.sh 0.3.0
set -eu
cd "$(dirname "$0")"
[ $# -eq 1 ] || { echo "usage: $0 <version>" >&2; exit 1; }
sed -i -e "s/^pkgver=.*/pkgver=$1/" -e "s/^pkgrel=.*/pkgrel=1/" PKGBUILD
updpkgsums
makepkg --printsrcinfo > .SRCINFO
rm -rf src ./*.AppImage
echo "PKGBUILD and .SRCINFO are at $1"
