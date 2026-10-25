# Cross-Platform Research (Avalonia 12, .NET 10)

Targets: Windows x64, Linux x64, macOS arm64 and x64.
All findings verified on **2026-10-25**.

## Summary of decisions

| Topic | Decision |
|---|---|
| UI | Avalonia **12.1.4** with the `avalonia.mvvm` template defaults: Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter and CommunityToolkit.Mvvm 8.4.2 |
| Bindings | Compiled bindings are on by default. Put `x:DataType` on every view. |
| App data | `Environment.SpecialFolder.ApplicationData` / `LocalApplicationData`, plus a manual XDG cache path on Linux |
| URLs and files | Avalonia `TopLevel.Launcher` and `TopLevel.StorageProvider` |
| Notifications | In-app `WindowNotificationManager` only for v1 |
| Packaging and updates | **Velopack** (MIT): Setup.exe on Windows, `.app`/`.pkg` on macOS, `.AppImage` on Linux. Add a signed release feed (Ed25519) on top. |
| CI | `windows-latest`, `ubuntu-latest`, `macos-latest` (arm64), `macos-15-intel` (x64) |

---

## C1. Avalonia 12 status and breaking changes that affect a new app

- **Description:** Avalonia 12.0 was released on **2026-04-07**. The latest stable is **12.1.4** (2026-10-09, GitHub release and NuGet), under the MIT licence. Breaking changes from 11 that matter for a new desktop app:
  1. **Target framework:** .NET 8+ only; .NET Framework and netstandard support was dropped. .NET 10 is recommended.
  2. **`AvaloniaUseCompiledBindingsByDefault` is now `true`:** XAML `{Binding}` means `CompiledBinding`. Every view needs `x:DataType`, or bindings fail at build time.
  3. **`Avalonia.Diagnostics` is removed:** It is replaced by `AvaloniaUI.DiagnosticsSupport`, and `AttachDevTools()` becomes `AttachDeveloperTools()`.
  4. **Binding plugin system removed:** DataAnnotations validation is **disabled by default**. Use `ObservableValidator` from CommunityToolkit, or opt back in.
  5. **Old dialogs removed:** `OpenFileDialog`, `OpenFolderDialog` and `SaveFileDialog` are gone. Only `IStorageProvider` remains.
  6. **Clipboard:** `IDataObject` is removed in favour of `DataTransfer`, plus async extensions such as `TryGetTextAsync` and `SetDataAsync`. `DoDragDrop` becomes `DoDragDropAsync`.
  7. **TopLevel:** Do not cast a visual to `TopLevel`. Use `TopLevel.GetTopLevel(visual)`.
  8. **Window decorations:** `SystemDecorations` is replaced by `WindowDecorations`. `ExtendClientAreaChromeHints`, `TitleBar` and `CaptionButtons` are removed. `WindowState` can no longer be set from a style.
  9. **Renames:** `TextBox.Watermark` becomes `PlaceholderText`.
  10. **Text shaping:** If you call `UseSkia()` explicitly, also add `Avalonia.HarfBuzz` and `UseHarfBuzz()`, or startup throws. `UsePlatformDetect()` handles this automatically.
  11. **Animations** stop on hidden controls.
  12. **Headless tests** require **xUnit v3** or NUnit 4.
  13. **Focus:** `GotFocus`/`LostFocus` use `FocusChangedEventArgs`. Touch and pen selection happens on release.
  14. **Windows:** Direct2D1 is removed.
- **Sources:**
  - https://docs.avaloniaui.net/docs/avalonia12-breaking-changes
  - https://avaloniaui.net/blog/avalonia-12
  - GitHub API, `AvaloniaUI/Avalonia` releases (12.1.4, 2026-10-09)
  - NuGet `avalonia` 12.1.4, licence expression MIT
- **Verification date:** 2026-10-25
- **Technical implications:** A greenfield app avoids most migration pain. The items that affect a new app are 2, 4, 5, 6, 8 and 12. Old blog posts and samples written for Avalonia 11 (dialogs, `Watermark`, `AttachDevTools`, reflection bindings) will not compile.
- **Confidence:** High (official documentation).
- **Limitations:** Wayland is still a private preview (blog), so Linux runs through X11/XWayland. The docs' macOS guide notes a known issue: the .NET 11 SDK RC2 supports only Xcode 26.0 when targeting .NET 10. This affects mobile and Catalyst workloads, not plain `osx-*` desktop publishing.
- **Selected implementation approach:** Target `net10.0` and Avalonia 12.1.x. Pin versions with central package management. Set `x:DataType` on every view and use `[ObservableProperty]` and `[RelayCommand]`. Use `UsePlatformDetect()` and do not call `UseSkia` manually. Use xUnit v3 for headless UI tests.

## C2. Project template, packages and MVVM

- **Description:** The `Avalonia.Templates` template `avalonia.mvvm` (template release 12.1.2, 2026-09-02; default `--avalonia-version` 12.1.4; default framework `net10.0`) creates a project that references these packages:
  - `Avalonia`
  - `Avalonia.Desktop`
  - `Avalonia.Themes.Fluent`
  - `Avalonia.Fonts.Inter`
  - `AvaloniaUI.DiagnosticsSupport` 2.2.3, with `IncludeAssets=None` and `PrivateAssets=All` outside Debug
  - `CommunityToolkit.Mvvm` 8.4.2

  `-m CommunityToolkit` is the default; ReactiveUI is the alternative. A ViewLocator is included unless `-rvl true` is passed.
- **Sources:**
  - https://github.com/AvaloniaUI/avalonia-dotnet-templates (README and `templates/csharp/app-mvvm/AvaloniaAppTemplate.csproj`, read through the GitHub API)
  - https://docs.avaloniaui.net/docs/get-started/
- **Verification date:** 2026-10-25
- **Technical implications:** `AvaloniaUI.DiagnosticsSupport` has **no licence expression** in its nuspec (proprietary AvaloniaUI OÜ). The template already excludes it from Release builds, so it is not shipped. See LICENSING.md, L3.
- **Confidence:** High.
- **Limitations:** None.
- **Selected implementation approach:** Run `dotnet new install Avalonia.Templates`, then `dotnet new avalonia.mvvm -o src/Ninja.App -m CommunityToolkit`. Keep the Debug-only condition on DiagnosticsSupport, or remove the package altogether if a fully free toolchain matters. Keep the ViewLocator but make it non-reflective: map ViewModels to Views with a switch statement, which stays safe for trimming.

## C3. Standard app data and cache directories

- **Description:** `Environment.GetFolderPath` behaviour on .NET 8+:

  | SpecialFolder | Windows | Linux | macOS |
  |---|---|---|---|
  | `ApplicationData` | `%APPDATA%` (Roaming) | `$XDG_CONFIG_HOME`, else `~/.config` | `~/Library/Application Support` |
  | `LocalApplicationData` | `%LOCALAPPDATA%` | `$XDG_DATA_HOME`, else `~/.local/share` | `~/Library/Application Support` |
  | `InternetCache` | `%LOCALAPPDATA%\Microsoft\Windows\INetCache` | **empty string** (no mapping) | `~/Library/Caches` |
  | `UserProfile` | `%USERPROFILE%` | `$HOME` | `$HOME` |

  On Linux, XDG variables are used only if they are absolute paths. On macOS, `ApplicationData` changed in .NET 8 (previously `~/.config`).
- **Sources:**
  - https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/8.0/getfolderpath-unix
  - https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Private.CoreLib/src/System/Environment.GetFolderPathCore.Unix.cs
- **Verification date:** 2026-10-25
- **Technical implications:** .NET has no cross-platform "cache" folder. Linux needs `$XDG_CACHE_HOME` or `~/.cache` read by hand. Game instances, assets and libraries are large, so put them under `LocalApplicationData`, not Roaming `%APPDATA%`.
- **Confidence:** High (official documentation plus runtime source).
- **Limitations:** Inside a Flatpak sandbox, the XDG variables point into `~/.var/app/<id>/`. This is expected.
- **Selected implementation approach:** Use one `AppPaths` static class:
  - **Data:** `LocalApplicationData/NinjaLauncher`. On macOS this resolves to `~/Library/Application Support/NinjaLauncher`.
  - **Cache:** `$XDG_CACHE_HOME`, else `~/.cache/NinjaLauncher` on Linux. `InternetCache/NinjaLauncher` on macOS. `%LOCALAPPDATA%\NinjaLauncher\Cache` on Windows.
  - **Override:** An environment variable or a `portable.txt` file next to the executable switches to portable mode.

## C4. Opening URLs and folders per OS

- **Description:** Avalonia's `TopLevel.GetTopLevel(control).Launcher` offers `LaunchUriAsync(Uri)`, `LaunchFileAsync(IStorageItem)`, and the desktop-only extensions `LaunchFileInfoAsync` and `LaunchDirectoryInfoAsync`. The OS decides whether to accept a request. A `true` result does not guarantee a handler exists. The plain .NET fallback is `Process.Start(new ProcessStartInfo(url){UseShellExecute=true})`. On Linux .NET tries `xdg-open`, `gnome-open` and `kfmclient`; on macOS it uses `/usr/bin/open`.
- **Sources:**
  - https://docs.avaloniaui.net/docs/services/launcher
  - https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Linux.cs
  - https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.OSX.cs
- **Verification date:** 2026-10-25
- **Technical implications:** No per-OS code is needed for http(s) links or folders. Custom schemes such as `minecraft://` or `ms-windows-store://` also go through `LaunchUriAsync`.
- **Confidence:** High.
- **Limitations:** On minimal Linux systems without `xdg-utils`, both paths fail. Show the URL so the user can copy it.
- **Selected implementation approach:** Use `Launcher.LaunchUriAsync` everywhere. If it returns `false`, show the URL with a copy button.

## C5. File and folder pickers

- **Description:** `TopLevel.StorageProvider` offers `OpenFilePickerAsync`, `SaveFilePickerAsync` and `OpenFolderPickerAsync`. The legacy dialog classes were removed in Avalonia 12 (C1 item 5). It uses native dialogs on all three desktop OSes. On Linux it goes through the xdg-desktop-portal or GTK, with a managed fallback.
- **Sources:**
  - https://docs.avaloniaui.net/docs/avalonia12-breaking-changes
  - https://docs.avaloniaui.net/docs/services/launcher (shows the picker-to-launcher pairing)
- **Verification date:** 2026-10-25
- **Technical implications:** Results are `IStorageFile` or `IStorageFolder`. Call `TryGetLocalPath()` to get a filesystem path for Java, mods and similar.
- **Confidence:** High.
- **Limitations:** I did not look up the exact Linux portal fallback order in the Avalonia 12 documentation.
- **Selected implementation approach:** Add a small `IDialogService` in the ViewModel layer, implemented with `StorageProvider`, so ViewModels can be tested without UI.

## C6. Native notifications

- **Description:** Avalonia ships only in-app notifications (`WindowNotificationManager`), not OS toasts. The best-known cross-platform library, `DesktopNotifications` (MIT; Windows toasts plus Linux FreeDesktop D-Bus), was **archived on 2026-01-12**, and its macOS backend never worked. The OS-native options are:
  - **Windows:** Toast notifications need a Windows TFM or COM interop plus an AUMID or shortcut.
  - **Linux:** The `org.freedesktop.Notifications` D-Bus interface, through `Tmds.DBus` (MIT) or `notify-send`.
  - **macOS:** `UNUserNotificationCenter` needs a signed `.app` bundle.
- **Sources:**
  - https://github.com/pr8x/DesktopNotifications
  - GitHub API `Tmds/Tmds.DBus` (MIT)
- **Verification date:** 2026-10-25
- **Technical implications:** Native notifications on all three OSes would mean three platform implementations, one of them depending on signing. That is too much work for the value.
- **Confidence:** High.
- **Limitations:** I did not evaluate newer forks of DesktopNotifications.
- **Selected implementation approach:** v1 uses `WindowNotificationManager` (in-app) only, plus flashing the taskbar or dock icon when the game exits or a download finishes. Native toasts are deferred.

## C7. Publishing self-contained binaries

- **Description:** Example command: `dotnet publish src/Ninja.App -c Release -r <win-x64|linux-x64|osx-arm64|osx-x64> --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`.
  - Without `IncludeNativeLibrariesForSelfExtract`, native libraries (SkiaSharp, HarfBuzz, e_sqlite3) sit **next to** the single file.
  - Trimming (`PublishTrimmed`) is risky with Avalonia because of reflection. Avalonia's docs warn about it, and community reports describe runtime crashes.
  - The Avalonia macOS guide suggests `-p:UseAppHost=true` and says single-file "simplifies signing and notarization".
- **Sources:**
  - https://docs.avaloniaui.net/docs/deployment/embedded-linux/
  - https://docs.avaloniaui.net/docs/deployment/macos
  - https://github.com/dotnet/sdk/issues/24181
- **Verification date:** 2026-10-25
- **Technical implications:** Self-extracting native libraries go to a temp directory at first run, and some antivirus tools flag this. Velopack does not need single-file, because it packs a whole folder.
- **Confidence:** Medium-high.
- **Limitations:** I did not test on .NET 10 for all four RIDs.
- **Selected implementation approach:** Publish **self-contained, not single-file, not trimmed** as input for Velopack. Keep the single-file publish only for an optional "portable zip". Turn on compiled bindings now; that is a prerequisite if trimming or NativeAOT is evaluated later.

## C8. macOS: .app bundle, signing, notarization

- **Description:**
  - **Bundle layout:** `Ninja Launcher.app/Contents/{Info.plist, MacOS/<publish output incl. apphost>, Resources/<icon>.icns, _CodeSignature}`.
  - **Required Info.plist keys:** `CFBundleExecutable` (matches the apphost name), `CFBundleName` (15 characters max; use `CFBundleDisplayName` for longer names), `CFBundleIdentifier` (reverse DNS), `CFBundleIconFile`, `CFBundleVersion`, `CFBundleShortVersionString`, `CFBundlePackageType=APPL`, `NSHighResolutionCapable=true`, `LSMinimumSystemVersion`. Optionally `CFBundleURLTypes` for a custom URL scheme such as a Microsoft-auth redirect.
  - **Signing:** Sign each file in `MacOS/`, then the bundle, with `codesign --force --timestamp --options=runtime --entitlements ent.plist --sign "Developer ID Application: ..."`. Avoid `--deep`. Avalonia apps need only the `com.apple.security.cs.allow-jit` entitlement.
  - **Notarization:** `xcrun notarytool submit app.zip --keychain-profile ... --wait`, then `xcrun stapler staple`. Zip with `ditto -c -k --sequesterRsrc --keepParent`.
  - **Cost:** Distributing outside the Mac App Store so that Gatekeeper accepts the app requires a **Developer ID** certificate. That needs the **Apple Developer Program at US$99 per year**. Fee waivers apply only to nonprofits, education and government, not individuals.
  - **Tooling:** Velopack does all of this with `--signAppIdentity`, `--signInstallIdentity` and `--notaryProfile`, but `vpk` **must run on macOS** for macOS packages. Avalonia "Parcel" can sign and notarize from Windows or Linux, but its CLI and CI support need a paid Avalonia Plus licence.
- **Sources:**
  - https://docs.avaloniaui.net/docs/deployment/macos
  - https://docs.avaloniaui.net/docs/platform-specific-guides/macos
  - https://docs.velopack.io/packaging/signing
  - https://docs.velopack.io/packaging/cross-compiling
  - https://developer.apple.com/forums/thread/746992
  - https://flaviocopes.com/apple-developer-free-vs-paid/
  - https://avaloniaui.net/parcel/
  - https://docs.avaloniaui.net/tools/parcel/command-line-reference
- **Verification date:** 2026-10-25
- **Technical implications:** Unsigned builds are blocked by Gatekeeper on Apple Silicon. The user has to right-click and choose Open, or use System Settings, then Privacy & Security, then "Open Anyway". One blog post (unconfirmed) says Homebrew's official cask repository now disables casks that fail Gatekeeper.
- **Confidence:** High for the steps. Medium for the exact fee in other currencies.
- **Limitations:** Apple's own pricing page was not fetched directly; the fee comes from forum and blog sources consistent with the long-standing US$99.
- **Selected implementation approach:** Build macOS packages on `macos-latest` (arm64) and `macos-15-intel` runners with `vpk pack`. Sign and notarize only when the secrets are present. Until the project funds a Developer ID, ship unsigned with clear "Open Anyway" instructions. Use universal binaries only if two downloads prove confusing.

## C9. Windows installers

- **Description:**
  - **Inno Setup:** Its licence allows use "for any purpose, including commercial applications" (copyright notice must be kept). The product page "requests" that commercial users buy a licence, but LICENSE.TXT does not require it. Free for this project.
  - **WiX Toolset:** Source is MS-RL. Since v6 the binaries are under the **Open Source Maintenance Fee (OSMF) EULA**, which requires organisations with more than US$10k annual revenue to sponsor the project. The FSF calls MS-RL GPL-incompatible, but WiX is a build tool and is not linked into our app, so this does not matter for licensing.
  - **Velopack:** MIT. Produces `Setup.exe`, a portable zip, full and delta `.nupkg` packages, and an optional MSI (MSI build needs Windows). Installs per user into `%LOCALAPPDATA%`, with no admin rights needed.
  - **Code signing:** Velopack supports `signtool` (`--signParams`), any tool via `--signTemplate`, and Azure Artifact (Trusted) Signing via `--azureTrustedSignFile`.
- **Sources:**
  - https://jrsoftware.org/files/is/license.txt
  - https://jrsoftware.org/isinfo.php
  - https://docs.firegiant.com/wix/osmf/
  - https://www.gnu.org/licenses/license-list.html
  - https://docs.velopack.io/packaging/overview
  - https://docs.velopack.io/packaging/signing
  - https://github.com/velopack/velopack (MIT; latest stable 1.2.161 on 2026-09-29; prerelease 1.3.39 on 2026-10-17)
- **Verification date:** 2026-10-25
- **Technical implications:** Unsigned `Setup.exe` files trigger SmartScreen warnings. A per-user install avoids UAC prompts.
- **Confidence:** High.
- **Limitations:** Velopack's docs mention 1.3 features such as cross-OS Azure signing, but the latest *stable* NuGet is 1.2.161.
- **Selected implementation approach:** Use Velopack `Setup.exe` with a per-user install. Inno Setup and WiX are not needed. Add Azure Artifact Signing or SignPath Foundation (free for OSS) later; until then ship unsigned.

## C10. Linux packaging

- **Description:**
  - **AppImage:** Velopack's only Linux output is a self-updating `.AppImage` ("There is no setup/installer package for Linux"). `appimagetool` is MIT-licensed.
  - **.deb and .rpm:** `nfpm` (MIT, from GoReleaser) builds `.deb`, `.rpm` and `.apk` with no extra dependencies.
  - **Flatpak:** Needs a manifest using `org.freedesktop.Sdk.Extension.dotnet10`, with branches 24.08, 25.08 and 26.08 on Flathub. Flathub requires builds from source with offline NuGet sources.
- **Sources:**
  - https://docs.velopack.io/packaging/overview
  - GitHub API: `AppImage/appimagetool` (LICENSE: MIT), `goreleaser/nfpm` (MIT), `flathub/org.freedesktop.Sdk.Extension.dotnet10` (branches)
- **Verification date:** 2026-10-25
- **Technical implications:**
  - AppImage needs FUSE 2 on the host, or `--appimage-extract-and-run`.
  - Velopack's self-update for AppImage conflicts with distro package managers, so `.deb` and Flatpak builds must turn the updater off.
  - Flatpak's sandbox complicates spawning Java and reaching `~/.minecraft`. It needs `--filesystem` permissions.
- **Confidence:** Medium-high.
- **Limitations:** Flathub's current .NET submission rules were not re-read.
- **Selected implementation approach:** v1 ships the Velopack AppImage plus a plain `tar.gz`. Add a `.deb` through nfpm in v1.x. Add Flatpak later as a community-maintained option, with the updater disabled through a build flag.

## C11. Secure update frameworks

- **Description:**
  - **Velopack (MIT):** Verifies a downloaded package only against the **SHA-256 listed in `releases.<channel>.json`**, which is served from the same place as the package (for example the same GitHub Release). So trust rests on TLS plus the GitHub account. No detached signature exists. The issue proposing a check that the new macOS bundle is signed by the same team before install, #975, is still an open enhancement. Velopack gives a good install and delta experience on all three OSes.
  - **NetSparkle (MIT, 3.1.0 on 2026-05-05):** Built-in **Ed25519** verification of appcast and update files (`Ed25519Checker`, `SecurityMode.Strict`), with keys generated by its AppCastGenerator. It has an Avalonia UI package, but the README says it is for Avalonia 11. It downloads installers (exe, msi, dmg, pkg, tar.gz, deb, rpm) but does not install or delta-patch in place.
  - **Ed25519 in .NET:** Not in the base class library. Usable libraries: `NSec.Cryptography` 26.4.0 (MIT, uses libsodium) and `BouncyCastle.Cryptography` 2.7.0 (MIT).
- **Sources:**
  - https://github.com/velopack/velopack/issues/975
  - https://docs.velopack.io/reference/cs/Velopack/VelopackAsset/properties/SHA256
  - https://github.com/NetSparkleUpdater/NetSparkle
  - NuGet nuspecs for nsec.cryptography and bouncycastle.cryptography
- **Verification date:** 2026-10-25
- **Technical implications:** A compromised GitHub token could publish a malicious release that Velopack would accept. Signing the feed with a key that is pinned in the app closes this gap. Because the feed lists each package's SHA-256, verifying the feed's signature also protects the packages.
- **Confidence:** High for NetSparkle and the Velopack hash behaviour. Medium for how easily the feed check can be wrapped: the Velopack `IUpdateSource` customisation was not prototyped.
- **Limitations:** Velopack 1.3 may add signature features; re-check before implementing.
- **Selected implementation approach:** Use **Velopack** for install and update. Add a thin custom update source that downloads `releases.<channel>.json` and its `.sig`, verifies the signature with an Ed25519 public key compiled into the app (using BouncyCastle, which is pure managed with no native library), and only then hands the feed to Velopack. Keep the private key in GitHub Actions secrets or offline. Also Authenticode-sign or codesign when certificates exist.

## C12. GitHub Actions runners (state on 2026-10-25)

- **Description:**

  | Label | Image | Arch |
  |---|---|---|
  | `windows-latest` | Windows Server 2025 | x64 |
  | `ubuntu-latest` | Ubuntu 24.04 (26.04 available as `ubuntu-26.04`) | x64 |
  | `macos-latest` | macOS 26 | arm64 |
  | `macos-15-intel` | macOS 15 | x64 |
  | `macos-26-intel` | macOS 26 | x64 (listed next to the large labels) |

  `macos-13` was retired by 2025-12-04, and `macos-14` is deprecated. GitHub's changelog said Intel macOS support ends "after the macOS 15 runner image is retired in Fall 2027". The runner-images README now also lists `macos-26-intel`.
- **Sources:**
  - https://github.com/actions/runner-images
  - https://github.blog/changelog/2025-09-19-github-actions-macos-13-runner-image-is-closing-down/
- **Verification date:** 2026-10-25
- **Technical implications:** osx-x64 builds can still be produced natively until about Fall 2027. After that, cross-publish `osx-x64` on arm64 runners. `dotnet publish -r osx-x64` works on arm64, and `vpk` only needs macOS, not an Intel Mac.
- **Confidence:** High for the labels. Medium for whether `macos-26-intel` is free for public repos or a paid large runner.
- **Limitations:** I did not check the billing details for `macos-26-intel`.
- **Selected implementation approach:** Use a CI matrix:
  - `windows-latest`: tests, win-x64 pack
  - `ubuntu-latest`: tests, linux-x64 pack
  - `macos-latest`: tests, osx-arm64 pack
  - `macos-latest`: osx-x64 cross-publish and pack, avoiding dependence on Intel images (`macos-15-intel` stays available as a fallback)

  Release jobs trigger on tags and upload to GitHub Releases.
