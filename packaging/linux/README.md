# Linux packages (.deb and Flatpak)

Both packages wrap the self-contained `linux-x64` publish output. CI builds and validates them on every push and pull request (`.github/workflows/ci.yml`, jobs `linux-packages` and `flatpak`), and a release tag attaches `ninja-launcher_<version>_amd64.deb` and `ninja-launcher-<version>-x86_64.flatpak` to the draft release, covered by `SHA256SUMS.txt`.

| File | Purpose |
| --- | --- |
| `build-packages.sh` | Builds the `.deb` and the Flatpak input tarball, and validates the desktop entry and AppStream metainfo |
| `io.github.m0nst3rg.NinjaLauncher.desktop` | Desktop entry (shared by both packages) |
| `io.github.m0nst3rg.NinjaLauncher.metainfo.xml` | AppStream metainfo; `@VERSION@` and `@DATE@` are filled in at build time |
| `../flatpak/io.github.m0nst3rg.NinjaLauncher.yml` | Flatpak manifest (org.freedesktop.Platform 26.08) |

Icons (32 to 512 px, hicolor) are generated from `branding/ninja-launcher-logo.png` at build time.

## Build locally (Debian or Ubuntu, x86_64)

```bash
sudo apt-get install imagemagick desktop-file-utils appstream    # plus the .NET 10 SDK
dotnet publish src/NinjaLauncher.Desktop -c Release -r linux-x64 --self-contained -o publish/linux-x64
bash packaging/linux/build-packages.sh publish/linux-x64 0.1.0
# -> artifacts/linux/ninja-launcher_0.1.0_amd64.deb and artifacts/linux/ninja-launcher-linux-x64.tar.gz
sudo apt-get install ./artifacts/linux/ninja-launcher_0.1.0_amd64.deb
```

A pre-release version such as `0.2.0-beta.1` becomes the Debian version `0.2.0~beta.1`, so it sorts before `0.2.0`.

Flatpak (run after the script, which makes the tarball the manifest installs; nothing is downloaded during the build):

```bash
flatpak install flathub org.freedesktop.Platform//26.08 org.freedesktop.Sdk//26.08
flatpak-builder --user --install --force-clean build-flatpak packaging/flatpak/io.github.m0nst3rg.NinjaLauncher.yml
flatpak run io.github.m0nst3rg.NinjaLauncher
```

## What is different from the Velopack AppImage

- **Updates.** The package manager (apt, `flatpak update`) owns updates. The in-app updater turns itself off: Velopack's `UpdateManager.IsInstalled` is only true inside a Velopack AppImage (it needs `$APPIMAGE` and the `UpdateNix` and `sq.version` files that `vpk pack` adds). A plain publish has none of these, so Settings shows "Updates only work in an installed copy". `build-packages.sh` refuses to package a publish folder that contains Velopack files. A tagged release does not yet publish an apt repository or a Flathub entry, so users update by installing the next release's file.
- **Sign-in storage.** Refresh tokens go to the Secret Service through the `secret-tool` CLI. The `.deb` recommends `libsecret-tools`, which provides it. The Flatpak runtime does not ship `secret-tool` (it is only in the SDK), so inside the Flatpak the launcher keeps sign-ins for the current session only and you sign in again after a restart. Fixing that needs a code change (talk to the Secret Service over D-Bus, or bundle libsecret's tool), not a packaging change.
- **Flatpak sandbox.** Permissions: network, X11 (Avalonia 12 renders through X11, XWayland on Wayland sessions) plus IPC, Wayland, GPU (`--device=dri`) and PulseAudio for the game. Data, instances and the Mojang Java runtimes live in `~/.var/app/io.github.m0nst3rg.NinjaLauncher/data/NinjaLauncher`, and the Java the launcher downloads runs from there. Files elsewhere are reached through the file chooser portal; links and folders open through the portal. There is no system Java in the sandbox, and an externally installed `mcpelauncher` (Bedrock) is not visible to it.
- **Install location.** The `.deb` installs to `/opt/ninja-launcher` with the `ninja-launcher` command in `/usr/bin`. Data stays in `~/.local/share/NinjaLauncher`, as in every other Linux build.
