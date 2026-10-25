# Bedrock Edition Feasibility

Research for Ninja Launcher (GPL-3.0-or-later, C#/.NET 10, Avalonia 12).
All findings verified on **2026-10-25** unless stated otherwise.

## Summary

| Platform | Feasible scope | Not allowed |
|---|---|---|
| Windows x64 | Detect the installed Store/Xbox package, launch it, open its data folders, manage packs/worlds the user owns | Bypassing the licence, decrypting or unpacking `Minecraft.Windows.exe`, side-loading, redistributing game files |
| Linux / macOS | Detect an externally installed `mcpelauncher` (Flatpak or native) and start it | Bundling mcpelauncher, downloading APKs, any Google Play work-around |

Bedrock support is **optional and launch-only**. The launcher never installs, patches or downloads Bedrock.

---

## B1. Bedrock for Windows moved from UWP to GDK in 1.21.120

- **Description:** Since version 1.21.120 (Preview 1.21.120.21, released in late 2025), Minecraft for Windows has been a GDK (Game Development Kit) application, not a UWP application. Game files moved out of `C:\Program Files\WindowsApps` into `<drive>\XboxGames\Minecraft for Windows\Content`. A Store install can use a GUID folder instead: `XboxGames\7792D9CE-355A-493C-AFBD-768F4A77C3B0\Content`. Preview uses `XboxGames\Minecraft Preview for Windows` or GUID `98BD2335-9B01-4E4C-BD05-CCC01614078B`. The main executable is `Minecraft.Windows.exe`, which is encrypted at rest, and a `gamelaunchhelper.exe` was added. The game is no longer sandboxed.
- **Sources:**
  - https://learn.microsoft.com/en-us/minecraft/creator/documents/gdkpcprojectfolder?view=minecraft-bedrock-stable (official; ms.date 2025-10-24, updated 2025-11-11)
  - https://minecraft.wiki/w/Microsoft.MinecraftUWP (community wiki)
  - https://jaylydev.github.io/posts/bedrock-gdk/ (community, 2025-11-29)
  - https://bedrock-native-modding.github.io/wiki/platforms/gdk.html (community)
- **Verification date:** 2026-10-25
- **Technical implications:** Do not hard-code `C:\Program Files\WindowsApps\...` or `XboxGames\...`. The install drive is chosen by the user in the Store or Xbox app. Get the install folder from the package API (B2), never by guessing paths.
- **Confidence:** High for the migration and the new data paths (official Microsoft Learn). Medium for the XboxGames folder names, GUIDs and executable names (community wiki only).
- **Limitations:** Bedrock is not installed on the machine used for this research, so nothing was tested live. Only a different GDK title, `Microsoft.MinecraftDungeons2`, was seen. It is listed by `Get-AppxPackage` with a normal package family name, which supports the claim that GDK games stay registered as packages.
- **Selected implementation approach:** Ask the OS package API for the install location. Use the XboxGames paths only as a fallback and only for display.

## B2. Package identity and detection

- **Description:** The GDK build keeps the old package identity. The package name is `Microsoft.MinecraftUWP` and the package family name (PFN) is **`Microsoft.MinecraftUWP_8wekyb3d8bbwe`**. A community modding wiki uses `(Get-AppxPackage "Microsoft.MinecraftUWP").InstallLocation` against the GDK build. Microsoft event-log hang and crash reports show package-relative AppId **`Game`**, while the pre-GDK UWP AppId was commonly `App`. Preview uses package name `Microsoft.MinecraftWindowsBeta`. The Store product ID is `9NBLGGH2JHXJ`.
- **Sources:**
  - https://bedrock-native-modding.github.io/wiki/platforms/gdk.html (PFN plus AppId "Game" on GDK)
  - https://minecraft.wiki/w/Microsoft.MinecraftUWP (package folder names for Release, Preview and Education)
  - https://learn.microsoft.com/en-us/answers/questions/3987865/minecraft-keeps-crashing-system-failing (event logs showing `PackageRelativeAppId Game`)
  - https://jaylydev.github.io/posts/bedrock-gdk/ (product ID)
  - https://learn.microsoft.com/en-us/powershell/module/appx/get-appxpackagemanifest
- **Verification date:** 2026-10-25
- **Technical implications:**
  - Legitimate detection options:
    1. `Windows.Management.Deployment.PackageManager.FindPackagesForUser("", "Microsoft.MinecraftUWP_8wekyb3d8bbwe")`. This needs a Windows-specific TFM such as `net10.0-windows10.0.19041.0`, which splits the build.
    2. Running `powershell -NoProfile -Command "Get-AppxPackage -Name Microsoft.MinecraftUWP | ConvertTo-Json"` and parsing `InstallLocation`, `Version` and `PackageFamilyName`.
    3. Reading the per-user AppModel repository in the registry at `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Microsoft.MinecraftUWP_*`. This layout is undocumented and can change.
  - Read the AppId at runtime instead of hard-coding it. Use `Get-StartApps | ? Name -like '*Minecraft*'` or `Get-AppxPackageManifest`, which reads `Package/Applications/Application/@Id`.
- **Confidence:** High that the PFN is unchanged (one community source plus Microsoft event logs; no official statement says it changed). Medium for the AppId `Game` (event logs, not tested locally).
- **Limitations:** No official Mojang or Microsoft document states the GDK PFN or AppId. I could not test it on a machine with Bedrock installed.
- **Selected implementation approach:** Keep the main project on `net10.0` and add a small Windows-only service behind `OperatingSystem.IsWindows()`. Its first version shells out to `powershell.exe Get-AppxPackage` and `Get-StartApps` and parses the JSON. This avoids a second TFM. Move to the `PackageManager` WinRT API only if PowerShell proves too slow or unreliable. Detect both `Microsoft.MinecraftUWP` (Release) and `Microsoft.MinecraftWindowsBeta` (Preview).

## B3. Launching Bedrock legitimately

- **Description:** There are four legitimate ways to start Bedrock:
  - (a) **AUMID activation** with `explorer.exe shell:AppsFolder\Microsoft.MinecraftUWP_8wekyb3d8bbwe!<AppId>`. On GDK the AppId is expected to be `Game` (B2).
  - (b) **The `minecraft:` URI protocol**, for example `minecraft://` or the undocumented `minecraft://?addExternalServer=Name|host:port`. It was registered by the UWP manifest. I found no official documentation, and no confirmation that the GDK build still registers it.
  - (c) **The Xbox app URI** `msxbox://game/?productId=9NBLGGH2JHXJ`, which opens the Xbox app page for the game rather than starting it.
  - (d) **The official Minecraft Launcher**, AUMID `Microsoft.4297127D64EC6_8wekyb3d8bbwe!Minecraft`, which was observed on the research machine and can start Bedrock itself.
- **Sources:**
  - https://jaylydev.github.io/posts/bedrock-gdk/ (msxbox and ms-windows-store URIs)
  - https://github.com/Daniel-Ric/Minecraft-Store-Redirect (community; says `minecraft://` URLs are "not officially documented")
  - https://feedback.minecraft.net/hc/en-us/community/posts/360077256371-More-use-for-the-minecraft-URL
  - Local check on the research machine: `Get-StartApps` showed `Microsoft.4297127D64EC6_8wekyb3d8bbwe!Minecraft`, and `HKCR\minecraft` was not present because Bedrock is not installed.
- **Verification date:** 2026-10-25
- **Technical implications:** AUMID activation needs the user's own licence and goes through Windows. This is the cleanest option. The URI protocol may be missing, so check that `HKCR\minecraft` exists before relying on it.
- **Confidence:** Medium. None of these methods were tested live against a GDK build.
- **Limitations:** No process handle comes back, so "is it running" has to be checked by process name (`Minecraft.Windows.exe`). That name comes from the community wiki.
- **Selected implementation approach:** Try methods in this order:
  1. AUMID through `Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{pfn}!{appId}") { UseShellExecute = false })`, with the AppId read at runtime.
  2. If that fails, `minecraft://` when `HKCR\minecraft` exists.
  3. Otherwise, offer a button that opens the Store or Xbox page (`ms-windows-store://pdp/?productid=9NBLGGH2JHXJ`).

  Never run `Minecraft.Windows.exe` directly.

## B4. World and pack locations (com.mojang)

- **Description:**
  - **GDK (current):** Shared packs live in `%APPDATA%\Minecraft Bedrock\Users\Shared\games\com.mojang`, which holds `behavior_packs`, `resource_packs`, `development_behavior_packs`, `development_resource_packs` and `development_skin_packs`. Per-user data, including `minecraftWorlds` and settings, lives in `%APPDATA%\Minecraft Bedrock\Users\<numeric id>\games\com.mojang`. Logs are in `%APPDATA%\Minecraft Bedrock\logs\`. Preview uses `%APPDATA%\Minecraft Bedrock Preview\...`.
  - **UWP (pre-1.21.120, legacy):** `%LOCALAPPDATA%\Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\LocalState\games\com.mojang`. Microsoft says user content "should be copied over" during migration.
- **Sources:**
  - https://learn.microsoft.com/en-us/minecraft/creator/documents/gdkpcprojectfolder?view=minecraft-bedrock-stable (official; Shared path, logs, Preview, legacy path)
  - https://jaylydev.github.io/posts/bedrock-gdk/ (per-user `Users\<random numbers>` folder for worlds)
- **Verification date:** 2026-10-25
- **Technical implications:** Several user folders can exist, one per signed-in account. The worlds folder is per user, while packs can be shared. Old worlds may still sit only in the UWP path on machines where the migration failed.
- **Confidence:** High for the Shared and logs paths (official). Medium for the per-user worlds path (community source; Microsoft's page only names `users\shared`).
- **Limitations:** What the numeric user-folder id means is not documented. Treat it as opaque.
- **Selected implementation approach:** List `%APPDATA%\Minecraft Bedrock\Users\*\games\com.mojang` and label `Shared` separately. Show the legacy UWP path as read-only when it exists ("legacy worlds found"). Write only to `development_*` or pack folders when the user explicitly imports a `.mcpack` or `.mcworld`. The simplest import is to open the file with the shell, which lets Minecraft import it itself.

## B5. What is off-limits

- **Description:** These are out of scope:
  - Decrypting or dumping `Minecraft.Windows.exe`. Community wikis document work-arounds; we deliberately do not use or link them.
  - DLL injection or loaders (for example the LeviLamina ecosystem).
  - Downgrading by side-loading old packages.
  - "Unlocker" projects that bypass the licence (for example the GitHub repo `CoelhoFZ/Minecraft-Bedrock-Free`).
  - Redistributing any game files.

  Minecraft Usage Guidelines provision CA-P-025265 forbids redistributing the game or altered game files (see LICENSING.md, L10).
- **Sources:**
  - https://www.minecraft.net/en-us/usage-guidelines (the official page timed out from the research environment; text read from the tracked copy at https://conductatlas.com/platform/minecraft/minecraft-usage-guidelines/, captured 2026-10-21)
  - https://bedrock-native-modding.github.io/wiki/platforms/gdk.html (documents the encryption; not followed)
- **Verification date:** 2026-10-25
- **Technical implications:** The Bedrock module must not read, copy or modify anything under the install `Content` folder, apart from optionally showing its location.
- **Confidence:** High.
- **Limitations:** None for the policy itself.
- **Selected implementation approach:** Add a code-review rule: the Bedrock service may only query packages, activate the AUMID or URI, and read or write user data folders. No file I/O is allowed under `InstallLocation`.

## B6. Linux and macOS: mcpelauncher (minecraft-linux)

- **Description:** mcpelauncher is "an unstable unofficial launcher for the Android version of Minecraft: Bedrock Edition on linux and macOS". It runs the Android build through a "fake JNI" layer.
  - **Licence:** GPL-3.0 for `mcpelauncher-manifest` and `mcpelauncher-ui-qt`. The GitHub API reports no licence for the `mcpelauncher-ui-manifest` meta repo.
  - **Maintenance:** Active. The latest tag is `v1.8.4-qt6` (2026-09-17), and the repo was pushed on 2026-10-25.
  - **Architectures:** Linux glibc on x86, x86_64, armhf/arm32 and arm64. macOS on x86_64 and arm64; arm64 is marked experimental.
  - **Distribution:** Flatpak (`io.mrarm.mcpelauncher`; the README says Flathub beta, the site says Flathub), Debian, Ubuntu and Fedora repositories, unofficial AUR packages, and macOS prebuilt binaries. The AppImage is deprecated and outdated.
  - **macOS:** The site says the launcher fully supports up to 1.21.71 and warns that Mojang will require OpenGL ES 3.1, while the macOS port tops out at ES 3.0.
  - **Ownership model:** "Ownership on the Google Play Store, not any other Store, is a hard requirement." The Google account used must own the game. Side-loading an APK is refused ("this would allow piracy which is forbidden in this project"). The Microsoft Store, iOS and Amazon purchases do not count.
- **Sources:**
  - https://github.com/minecraft-linux/mcpelauncher-manifest
  - https://minecraft-linux.github.io/
  - https://minecraft-linux.github.io/faq/index.html
  - GitHub API (`gh api repos/minecraft-linux/...`) for licence and dates
- **Verification date:** 2026-10-25
- **Technical implications:** Bundling it would make our distribution carry its GPL-3.0 code. That is licence-compatible with GPL-3.0-or-later, but it would make us responsible for a Google Play download and login flow and for an unstable Android-emulation layer. Players would also need a second purchase. macOS support is falling behind the current game.
- **Confidence:** High for the facts above, which come from first-party project pages.
- **Limitations:** I did not test which Minecraft versions currently work on each architecture. The Flatpak channel (beta or stable) is inconsistent between the README and the site.
- **Selected implementation approach:** **Detect and launch only, never bundle or download.**
  - On Linux, check `flatpak info io.mrarm.mcpelauncher`, then `mcpelauncher-ui-qt` on `PATH`. Launch with `flatpak run io.mrarm.mcpelauncher` or the binary.
  - On macOS, check for `/Applications/Minecraft Bedrock Launcher.app` and launch it with `open -a`. The exact bundle name is unverified, so confirm it on a real install.
  - If nothing is found, show a short note that links to https://minecraft-linux.github.io/ and states the Google Play ownership requirement.
  - No Google credentials ever pass through Ninja Launcher.

## B7. Overall conclusion

- **Description:** Bedrock integration is feasible as a thin "detect, launch and open folders" feature on Windows, and as "detect and launch the external mcpelauncher" on Linux and macOS.
- **Source:** Findings B1 to B6 above.
- **Verification date:** 2026-10-25
- **Technical implications:** The work is small: one `IBedrockService` with a Windows implementation and a Unix implementation. It needs no new dependencies.
- **Confidence:** Medium-high. The PFN and AppId need one live test on a GDK install before release.
- **Limitations:** Microsoft can change the GDK packaging without notice, so keep all identifiers in one constants file.
- **Selected implementation approach:** Ship it in a later milestone, after Java Edition. Before coding, check these three items on a real Windows machine with Bedrock installed:
  1. The output of `Get-AppxPackage Microsoft.MinecraftUWP`.
  2. The AUMID returned by `Get-StartApps`.
  3. Whether `HKCR\minecraft` exists.
