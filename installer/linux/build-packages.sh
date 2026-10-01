#!/usr/bin/env bash
# Builds the Linux installers from a linux-x64 publish folder:
#   Packwright-v<version>-linux-x64.deb   (Debian, Ubuntu, Mint, ...)
#   Packwright-v<version>-linux-x64.rpm   (Fedora, RHEL family, openSUSE, ...)
#
# Usage: installer/linux/build-packages.sh <version> <publish folder> <output folder>
# Needs dpkg-deb and rpmbuild (apt install dpkg rpm). Both packages put the app in /opt/packwright, add a
# `packwright` command and a menu entry, and are removed again by the normal package manager.
set -euo pipefail

version="${1:?version}"
publish="$(cd "${2:?publish folder}" && pwd)"
mkdir -p "${3:?output folder}"
out="$(cd "$3" && pwd)"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
name="Packwright-v${version}-linux-x64"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
stage="$work/stage"

install -d "$stage/opt/packwright" "$stage/usr/bin" "$stage/usr/share/applications" \
  "$stage/usr/share/icons/hicolor/512x512/apps" "$stage/usr/share/doc/packwright"
cp -a "$publish/." "$stage/opt/packwright/"
chmod 755 "$stage/opt/packwright/Packwright"
ln -s /opt/packwright/Packwright "$stage/usr/bin/packwright"
install -m 644 "$here/packwright.desktop" "$stage/usr/share/applications/packwright.desktop"
install -m 644 "$repo/src/Packwright.App/Assets/icon.png" "$stage/usr/share/icons/hicolor/512x512/apps/packwright.png"
install -m 644 "$repo/LICENSE" "$stage/usr/share/doc/packwright/copyright"
install -m 644 "$repo/THIRD_PARTY_NOTICES.md" "$stage/usr/share/doc/packwright/THIRD_PARTY_NOTICES.md"
find "$stage" -type d -exec chmod 755 {} +
find "$stage" -type f ! -perm /111 -exec chmod 644 {} +

# ------------------------------------------------------------------ .deb
debroot="$work/deb"
cp -a "$stage" "$debroot"
install -d "$debroot/DEBIAN"
size_kb="$(du -sk "$debroot" | cut -f1)"
cat > "$debroot/DEBIAN/control" <<CONTROL
Package: packwright
Version: ${version}
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: ${size_kb}
Depends: libc6, libstdc++6, libx11-6, libice6, libsm6, libfontconfig1, libssl3 | libssl3t64 | libssl1.1
Maintainer: itsmemac <noreply@users.noreply.github.com>
Homepage: https://github.com/itsmemac/Packwright
Description: PS5 package, dump and image tool
 Manage a collection of PS5 dumps and images, read PS5 packages, and build or
 convert exFAT, FFPKG, FFPFSC, ZArchive images and debug packages.
 Starts from the menu, or from a terminal as "packwright" (try "packwright help").
CONTROL
chmod 644 "$debroot/DEBIAN/control"
dpkg-deb --root-owner-group -Zxz --build "$debroot" "$out/$name.deb"

# ------------------------------------------------------------------ .rpm
top="$work/rpm"
mkdir -p "$top"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
cat > "$top/SPECS/packwright.spec" <<SPEC
Name:       packwright
Version:    ${version}
Release:    1
Summary:    PS5 package, dump and image tool
License:    GPL-3.0-only
URL:        https://github.com/itsmemac/Packwright
AutoReqProv: no
Requires:   libstdc++.so.6()(64bit), libX11.so.6()(64bit), libICE.so.6()(64bit), libSM.so.6()(64bit), libfontconfig.so.1()(64bit), libssl.so.3()(64bit)

%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none
%define _binary_payload w9.xzdio

%description
Manage a collection of PS5 dumps and images, read PS5 packages, and build or
convert exFAT, FFPKG, FFPFSC, ZArchive images and debug packages.
Starts from the menu, or from a terminal as "packwright" (try "packwright help").

%install
cp -a ${stage}/. %{buildroot}/

%files
%defattr(-,root,root,-)
/opt/packwright
/usr/bin/packwright
/usr/share/applications/packwright.desktop
/usr/share/icons/hicolor/512x512/apps/packwright.png
/usr/share/doc/packwright
SPEC
rpmbuild -bb --target x86_64 --define "_topdir $top" "$top/SPECS/packwright.spec"
cp "$top/RPMS/x86_64/packwright-${version}-1.x86_64.rpm" "$out/$name.rpm"

echo "Built:"
ls -l "$out/$name.deb" "$out/$name.rpm"
