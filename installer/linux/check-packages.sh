#!/usr/bin/env bash
# Checks the Linux downloads that build-packages.sh made before they are published:
#   - the Arch package installs with pacman in an Arch Linux container, passes `pacman -Qkk`, and its
#     `packwright version` prints the expected version;
#   - the AppImage starts (extract-and-run, so no FUSE is needed) and prints the same version. Run this script as
#     an ordinary user (GitHub's runner user is one) so the AppImage is checked the way people start it.
#
# Usage: installer/linux/check-packages.sh <version> <output folder>
# Needs docker (present on GitHub's ubuntu runners).
set -euo pipefail

version="${1:?version}"
out="$(cd "${2:?output folder}" && pwd)"
name="Packwright-v${version}-linux-x64"
expected="Packwright ${version}"

echo "== Arch package"
docker run --rm -v "$out:/dist:ro" -e EXPECTED="$expected" -e PKG="/dist/$name.pkg.tar.zst" archlinux:latest bash -euo pipefail -c '
  # The archlinux image skips /usr/share/doc and similar (NoExtract) to stay small; a real install does not.
  sed -i "/^NoExtract/d" /etc/pacman.conf
  pacman -Syu --noconfirm --needed >/dev/null
  pacman -U --noconfirm "$PKG"
  pacman -Qi packwright
  pacman -Qkk packwright
  test -x /opt/packwright/Packwright
  test -f /usr/share/applications/packwright.desktop
  # Run it as an ordinary user: files only root can read would pass as root and fail for everyone else.
  useradd -m checker
  got="$(su checker -c "packwright version")"
  echo "packwright version (as a normal user): $got"
  [ "$got" = "$EXPECTED" ]
  pacman -R --noconfirm packwright
  test ! -e /opt/packwright/Packwright
'

echo "== AppImage"
appimage="$out/$name.AppImage"
head -c 4 "$appimage" | grep -q "ELF" || { echo "the AppImage does not start with the runtime" >&2; exit 1; }
got="$(APPIMAGE_EXTRACT_AND_RUN=1 "$appimage" version)"
echo "AppImage version: $got"
[ "$got" = "$expected" ]

echo "Linux packages OK"
