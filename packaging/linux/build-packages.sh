#!/usr/bin/env bash
# Builds the Debian package and the Flatpak input tarball from a self-contained linux-x64 publish.
#   packaging/linux/build-packages.sh <publish-dir> <version> [out-dir]
# Needs: dpkg-deb, ImageMagick, desktop-file-validate (desktop-file-utils), appstreamcli (appstream).
# Writes <out-dir>/ninja-launcher_<debversion>_amd64.deb and <out-dir>/ninja-launcher-linux-x64.tar.gz
# (the tarball is what packaging/flatpak/io.github.m0nst3rg.NinjaLauncher.yml installs).
set -euo pipefail

PUBLISH="${1:?usage: build-packages.sh <publish-dir> <version> [out-dir]}"
VERSION="${2:?usage: build-packages.sh <publish-dir> <version> [out-dir]}"
OUT="${3:-artifacts/linux}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ID=io.github.m0nst3rg.NinjaLauncher
LOGO="$ROOT/branding/ninja-launcher-logo.png"

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || { echo "version '$VERSION' is not MAJOR.MINOR.PATCH[-pre]" >&2; exit 1; }
test -f "$PUBLISH/NinjaLauncher" || { echo "$PUBLISH/NinjaLauncher not found; run dotnet publish -r linux-x64 --self-contained first" >&2; exit 1; }
# The package manager owns updates. Velopack only self-updates when its UpdateNix/sq.version files are present,
# so a .deb or Flatpak built from a Velopack package (instead of a plain publish) would try to update itself.
for f in UpdateNix sq.version; do
  test ! -e "$PUBLISH/$f" || { echo "$PUBLISH/$f is a Velopack file; package a plain dotnet publish output" >&2; exit 1; }
done
for t in dpkg-deb desktop-file-validate appstreamcli; do
  command -v "$t" >/dev/null || { echo "$t is required (Debian/Ubuntu: apt-get install dpkg desktop-file-utils appstream)" >&2; exit 1; }
done
IM="$(command -v magick || command -v convert)" || { echo "ImageMagick is required" >&2; exit 1; }

# Debian sorts '~' before anything, so 1.0.0-beta.1 becomes 1.0.0~beta.1 and stays older than 1.0.0.
DEB_VERSION="$(sed 's/-/~/' <<<"$VERSION")"
DATE="$(git -C "$ROOT" log -1 --format=%cs 2>/dev/null || date -u +%F)"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

# Shared stage: app/ (the publish output) and share/ (desktop entry, icons, metainfo).
STAGE="$WORK/ninja-launcher"
mkdir -p "$STAGE/share/applications" "$STAGE/share/metainfo"
cp -a "$PUBLISH" "$STAGE/app"
cp "$HERE/$ID.desktop" "$STAGE/share/applications/"
sed -e "s/@VERSION@/$VERSION/" -e "s/@DATE@/$DATE/" "$HERE/$ID.metainfo.xml" > "$STAGE/share/metainfo/$ID.metainfo.xml"
for s in 32 48 64 128 256 512; do
  d="$STAGE/share/icons/hicolor/${s}x${s}/apps"
  mkdir -p "$d"
  "$IM" "$LOGO" -resize "${s}x${s}" -background none -gravity center -extent "${s}x${s}" "$d/$ID.png"
done
# Artifact downloads drop the executable bit, so normalise every mode here.
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE" -type f -exec chmod 644 {} +
chmod 755 "$STAGE/app/NinjaLauncher"
test ! -e "$STAGE/app/createdump" || chmod 755 "$STAGE/app/createdump"

desktop-file-validate "$STAGE/share/applications/$ID.desktop"
appstreamcli validate --no-net "$STAGE/share/metainfo/$ID.metainfo.xml"

# Flatpak input: one top-level folder, so the manifest's archive source strips it.
tar -C "$WORK" --owner=0 --group=0 -czf "$OUT/ninja-launcher-linux-x64.tar.gz" ninja-launcher

# Debian package.
PKG="$WORK/deb"
mkdir -p "$PKG/DEBIAN" "$PKG/opt" "$PKG/usr/bin" "$PKG/usr/share/doc/ninja-launcher"
cp -a "$STAGE/app" "$PKG/opt/ninja-launcher"
cp -a "$STAGE/share" "$PKG/usr/"
printf '#!/bin/sh\nexec /opt/ninja-launcher/NinjaLauncher "$@"\n' > "$PKG/usr/bin/ninja-launcher"
chmod 755 "$PKG/usr/bin/ninja-launcher"
cat > "$PKG/usr/share/doc/ninja-launcher/copyright" <<'EOF'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: Ninja Launcher
Source: https://github.com/M0nsT3rG/Ninja-Launcher

Files: *
Copyright: 2026 Ninja Launcher contributors
License: GPL-3.0-or-later
 On Debian systems, the full text of the GNU General Public License version 3 can be found in
 /usr/share/common-licenses/GPL-3. The bundled .NET runtime and NuGet libraries keep their own (MIT) licences.
EOF
chmod 644 "$PKG/usr/share/doc/ninja-launcher/copyright"
# Self-contained .NET needs libc, libgcc, libstdc++, ICU, OpenSSL and zlib. Avalonia's X11 backend loads libX11,
# libICE, libSM, libXext, libXi, libXrandr, libXcursor and libXfixes; SkiaSharp needs fontconfig. ICU and OpenSSL
# alternatives cover Debian 12/13 and Ubuntu 22.04+ (.NET finds any libicu version at run time; add new ones here).
cat > "$PKG/DEBIAN/control" <<EOF
Package: ninja-launcher
Version: $DEB_VERSION
Architecture: amd64
Maintainer: M0nsT3rG <56177619+M0nsT3rG@users.noreply.github.com>
Installed-Size: $(du -sk --exclude=DEBIAN "$PKG" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70, libssl3t64 | libssl3, libfontconfig1, libx11-6, libice6, libsm6, libxext6, libxi6, libxrandr2, libxcursor1, libxfixes3
Recommends: libsecret-tools, xdg-utils, libgl1
Section: games
Priority: optional
Homepage: https://github.com/M0nsT3rG/Ninja-Launcher
Description: Open-source launcher for Minecraft: Java Edition
 Installs and launches Minecraft: Java Edition with Fabric, Quilt, Forge and
 NeoForge, and installs mods and modpacks from Modrinth. Each instance lives in
 its own folder. Refresh tokens are kept in the Secret Service through
 secret-tool (libsecret-tools); without it, sign-ins last one session.
 .
 NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG
 OR MICROSOFT.
EOF
DEB="$OUT/ninja-launcher_${DEB_VERSION}_amd64.deb"
# xz, not Ubuntu's default zstd, so older Debian dpkg can install it too.
dpkg-deb --build --root-owner-group -Zxz "$PKG" "$DEB"

dpkg-deb --info "$DEB"
CONTENTS="$(dpkg-deb --contents "$DEB")"
for p in ./opt/ninja-launcher/NinjaLauncher ./usr/bin/ninja-launcher "./usr/share/applications/$ID.desktop" \
         "./usr/share/metainfo/$ID.metainfo.xml" "./usr/share/icons/hicolor/256x256/apps/$ID.png"; do
  grep -q " $p\$" <<<"$CONTENTS" || { echo "missing from .deb: $p" >&2; exit 1; }
done
grep -E "^-rwxr-xr-x root/root .* \./(opt/ninja-launcher/NinjaLauncher|usr/bin/ninja-launcher)$" <<<"$CONTENTS" | wc -l | grep -qx 2 \
  || { echo "launcher files in the .deb are not root-owned 0755" >&2; exit 1; }
echo "Built $DEB and $OUT/ninja-launcher-linux-x64.tar.gz"
