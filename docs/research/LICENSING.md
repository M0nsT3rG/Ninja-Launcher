# Licensing Research

Project licence: **GPL-3.0-or-later**.
All findings verified on **2026-10-25**. Licence data for NuGet packages comes from each package's `.nuspec` on api.nuget.org (latest stable version on that date).

## Summary

**No licence incompatibility was found** among the candidate runtime dependencies. Each dependency is MIT, Apache-2.0, public domain or OFL-1.1 (OFL only for the font). All are GPLv3-compatible or can be shipped alongside GPLv3 code. Three points need attention:

1. `AvaloniaUI.DiagnosticsSupport` is proprietary and has no licence. Keep it Debug-only (the template default) or drop it.
2. Apache-2.0 dependencies (Serilog, Dapper, SQLitePCLRaw, xunit) rule out **GPLv2-only** relicensing. That is fine because the project is GPLv3+.
3. `System.Security.Cryptography.ProtectedData` is MIT but only works on Windows. This is a technical limit, not a legal one.

---

## L1. Dependency licence matrix

- **Description:**

  | Package | Version checked | Licence | GPLv3 compatible | Ships in app |
  |---|---|---|---|---|
  | Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.4 | MIT | Yes | Yes |
  | Avalonia.Fonts.Inter (wrapper) | 12.1.4 | MIT (package); embedded Inter font is OFL-1.1 | Yes, as an aggregate (L4) | Yes |
  | CommunityToolkit.Mvvm | 8.4.2 | MIT | Yes | Yes |
  | Microsoft.Data.Sqlite | 10.0.14 | MIT | Yes | Yes |
  | SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Apache-2.0 | Yes (v3 only) | Yes |
  | SQLitePCLRaw.lib.e_sqlite3 (native SQLite) | 3.53.3 | "SQLite is Public Domain" (LICENSE.txt) | Yes | Yes |
  | Serilog | 4.4.0 | Apache-2.0 | Yes (v3 only) | Yes |
  | Serilog.Sinks.File | 7.0.0 | Apache-2.0 | Yes (v3 only) | Yes |
  | Dapper | 2.1.89 | Apache-2.0 | Yes (v3 only) | Yes |
  | CmlLib.Core | 4.1.0 | MIT | Yes | Yes |
  | CmlLib.Core.Auth.Microsoft | 3.4.0 | MIT | Yes | Yes |
  | System.Security.Cryptography.ProtectedData | 10.0.14 | MIT | Yes | Yes (Windows only at runtime) |
  | Velopack | 1.2.161 | MIT | Yes | Yes |
  | NetSparkleUpdater.SparkleUpdater (alternative) | 3.1.0 | MIT | Yes | Not selected |
  | BouncyCastle.Cryptography (Ed25519 feed check) | 2.7.0 | MIT | Yes | Yes |
  | NSec.Cryptography (alternative) | 26.4.0 | MIT | Yes | Not selected |
  | Tmds.DBus.Protocol (optional, Linux notifications) | 0.95.1 | MIT | Yes | Deferred |
  | xunit.v3 / xunit | 4.0.2 / 2.9.3 | Apache-2.0 | Yes | No (tests only) |
  | AvaloniaUI.DiagnosticsSupport | 2.2.3 | **None declared**; "Copyright 2019-2026 © AvaloniaUI OÜ" | n/a | **No** (Debug only) |

- **Sources:**
  - `https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.nuspec` for each package above
  - Repo licences from the GitHub API: AvaloniaUI/Avalonia MIT, CmlLib/CmlLib.Core MIT, velopack/velopack MIT, NetSparkleUpdater/NetSparkle MIT, serilog/serilog Apache-2.0, ericsink/SQLitePCL.raw Apache-2.0
  - https://github.com/CmlLib/CmlLib.Core
- **Verification date:** 2026-10-25
- **Technical implications:**
  - Every permissive licence above needs its notice reproduced: MIT copyright lines, and Apache-2.0 licence text plus any NOTICE file.
  - The binary distribution needs a `THIRD-PARTY-NOTICES.txt`.
  - Transitive dependencies need the same audit. Velopack depends on Newtonsoft.Json (MIT) and Microsoft.Win32.Registry (MIT). NetSparkle depends on NetSparkleUpdater.Chaos.NaCl.
- **Confidence:** High (machine-read nuspec licence expressions).
- **Limitations:** Transitive dependencies were not fully enumerated. Run `dotnet list package --include-transitive` plus a licence scanner (for example `nuget-license`) in CI.
- **Selected implementation approach:**
  - Generate `THIRD-PARTY-NOTICES.txt` in CI from the resolved package graph and fail on any licence outside this allow-list: MIT, Apache-2.0, BSD-2/3-Clause, ISC, Zlib, OFL-1.1, Unlicense, public domain, LGPL-2.1+/3, GPL-3.0+.
  - Write the licence header as `SPDX-License-Identifier: GPL-3.0-or-later`.

## L2. Apache-2.0 and the GPL

- **Description:** The FSF says Apache-2.0 is "compatible with version 3 of the GNU GPL" but **not** with GPLv2, because of Apache's patent-termination and indemnification terms. Expat/MIT is "compatible with the GNU GPL". Public-domain material is compatible.
- **Source:** https://www.gnu.org/licenses/license-list.html
- **Verification date:** 2026-10-25
- **Technical implications:** The "or-later" clause has to stay at v3 or above. The project can never offer a GPLv2-only build while it uses Serilog, Dapper or SQLitePCLRaw.
- **Confidence:** High.
- **Limitations:** None.
- **Selected implementation approach:** Use GPL-3.0-or-later as planned. Put the full GPLv3 text in `LICENSE`.

## L3. Avalonia: free core vs. paid add-ons

- **Description:**
  - **Free (MIT):** The core framework and template packages. WebView became open source in 12.0.
  - **Paid:** Pro Components (Charts, TreeDataGrid Pro, PDF Viewer), Dev Tools and Hot Reload subscriptions, and the Parcel CLI (needs Avalonia Plus).
  - **Proprietary but used in templates:** `AvaloniaUI.DiagnosticsSupport` declares no licence. It is the bridge to the commercial Developer Tools.
- **Sources:**
  - https://avaloniaui.net/blog/avalonia-12
  - https://docs.avaloniaui.net/tools/parcel/command-line-reference
  - nuspec of avaloniaui.diagnosticssupport 2.2.3
- **Verification date:** 2026-10-25
- **Technical implications:** Shipping a proprietary DLL inside a GPL binary would conflict with the GPL. The GPL system-library exception does not cover it.
- **Confidence:** High.
- **Limitations:** None.
- **Selected implementation approach:**
  - Keep the template condition `IncludeAssets=None` and `PrivateAssets=All` outside Debug, and verify in CI that the Release output contains no `AvaloniaUI.DiagnosticsSupport.dll`.
  - Simpler still, remove the package. Contributors who want the dev tools can add it locally.
  - Use no Avalonia Pro components.

## L4. Inter font (SIL OFL-1.1)

- **Description:** Inter (rsms/inter) is licensed under OFL-1.1, as reported by the GitHub API. `Avalonia.Fonts.Inter` embeds the font files in an MIT-licensed DLL. The FSF calls OFL-1.1 "a free copyleft license for fonts". OFL explicitly allows bundling, embedding and redistributing fonts with any software, as long as the font itself stays under OFL, is not sold on its own, and modified versions do not use its Reserved Font Name.
- **Sources:**
  - https://www.gnu.org/licenses/license-list.html
  - https://github.com/rsms/inter
  - Package inspection of avalonia.fonts.inter 12.1.4 (the font is embedded in `lib/net*/Avalonia.Fonts.Inter.dll`)
- **Verification date:** 2026-10-25
- **Technical implications:** The font is a separate work aggregated with the GPL program, not a derivative of it. Include the OFL text in the third-party notices.
- **Confidence:** High.
- **Limitations:** The `Avalonia.Fonts.Inter` package itself does not include the OFL text, so we must add it ourselves.
- **Selected implementation approach:** Use the package unmodified. Add the Inter copyright line and the OFL-1.1 text to `THIRD-PARTY-NOTICES.txt`.

## L5. CmlLib.Core

- **Description:** CmlLib.Core 4.1.0 and CmlLib.Core.Auth.Microsoft 3.4.0 are both MIT. The repo is active (pushed 2026-10-25). Features include game install, Java runtime install and Microsoft Xbox account login. The last GitHub *release* tag is v4.0.4 (2025-01-03), but NuGet already has 4.1.0.
- **Sources:**
  - https://github.com/CmlLib/CmlLib.Core
  - NuGet nuspecs (cmllib.core 4.1.0, cmllib.core.auth.microsoft 3.4.0)
- **Verification date:** 2026-10-25
- **Technical implications:** It is compatible with GPLv3. It downloads game files from Mojang's official endpoints onto the user's machine at runtime, which is not redistribution by us.
- **Confidence:** High.
- **Limitations:** Its transitive dependencies (for example XboxAuthNet) need the same licence check.
- **Selected implementation approach:** Allowed. Wrap it behind our own interfaces so it can be replaced.

## L6. System.Security.Cryptography.ProtectedData

- **Description:** Licensed MIT (10.0.14). It wraps Windows DPAPI and throws `PlatformNotSupportedException` on Linux and macOS.
- **Source:** nuspec of system.security.cryptography.protecteddata 10.0.14
- **Verification date:** 2026-10-25
- **Technical implications:** Refresh tokens on Linux and macOS need a different store: macOS Keychain, Linux Secret Service over D-Bus, or an encrypted file as a last resort.
- **Confidence:** High for the licence. The platform limit is well known; I did not re-read the docs for it this session.
- **Limitations:** Cross-platform secret storage was not researched here.
- **Selected implementation approach:** Use ProtectedData on Windows only. Put token storage behind an `ISecretStore` interface whose Linux and macOS implementations come in a separate task.

## L7. Build and packaging tools (not linked into the app)

- **Description:**
  - **Velopack:** MIT.
  - **Inno Setup:** Free "for any purpose, including commercial applications". Copyright notices must be kept.
  - **WiX v6 and later:** Source under MS-RL, which the FSF calls GPL-incompatible. Binaries require the OSMF EULA (sponsorship when revenue is above US$10k).
  - **appimagetool:** MIT.
  - **nfpm:** MIT.
- **Sources:**
  - https://jrsoftware.org/files/is/license.txt
  - https://docs.firegiant.com/wix/osmf/
  - https://www.gnu.org/licenses/license-list.html
  - GitHub API LICENSE files for AppImage/appimagetool and goreleaser/nfpm
- **Verification date:** 2026-10-25
- **Technical implications:** Build tools do not affect the licence of the produced program, unless they inject runtime code. Velopack's runtime is MIT, so it is fine either way.
- **Confidence:** High.
- **Limitations:** None.
- **Selected implementation approach:** Use Velopack plus appimagetool (bundled by Velopack) plus nfpm. Avoid WiX.

## L8. Minecraft Usage Guidelines: mandatory disclaimer (exact wording)

- **Description:** The guidelines say: "Prominently include the disclaimer similar to the following:" followed by **"NOT AN OFFICIAL MINECRAFT [PRODUCT/SERVICE/EVENT/etc.]. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT"**. The disclaimer must be prominent (provision CA-P-025271). A separate provision requires disclosing that *we*, not Mojang, are the responsible party (CA-P-025273). For this project, the wording becomes **`NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.`**, which matches the wording in the task brief.
- **Sources:**
  - https://www.minecraft.net/en-us/usage-guidelines (official; **timed out three times** from the research environment, so not read directly)
  - Tracked verbatim copy: https://conductatlas.com/platform/minecraft/minecraft-usage-guidelines/provision/CA-P-025271/mandatory-not-official-disclaimer-requirement/ (version CA-V-006648, 2026-09-11; unchanged in the 2026-10-21 capture)
  - Consistent with https://education.minecraft.net/en-us/get-started/partners
- **Verification date:** 2026-10-25
- **Technical implications:** The disclaimer must appear in the app (About screen and first-run screen or footer), README, website, store listings and release notes.
- **Confidence:** High for the wording, because two independent sources agree and match the brief. Medium that it is the *current* official text, since the official page could not be fetched directly.
- **Limitations:** Open https://www.minecraft.net/en-us/usage-guidelines in a browser and confirm the wording before the first public release.
- **Selected implementation approach:** Keep a constant `Branding.Disclaimer` containing exactly that string. Show it in the About dialog, the main window footer and the README, plus a responsible-party line: "Ninja Launcher is developed by its contributors; Mojang and Microsoft are not responsible for it."

## L9. Minecraft Usage Guidelines: naming and branding

- **Description:** These provisions apply to a third-party launcher (from the tracked copy captured 2026-10-21):
  - The Minecraft name may be used only as a **secondary or descriptive element, never the dominant or primary title** (CA-P-025281).
  - The Minecraft brand must not appear in a **logo** (CA-P-025285), and no Minecraft logo or **look-alike lettering** may be used (CA-P-025393).
  - The **domain name** must not look official (CA-P-025297).
  - Nothing may give the impression of **official association or endorsement** (CA-P-025259). The test is how people perceive it.
  - Permissions are revocable, and the guidelines can change at any time (CA-P-025247, CA-P-025249).
- **Sources:**
  - https://conductatlas.com/platform/minecraft/minecraft-usage-guidelines/
  - https://www.minecraft.net/en-us/usage-guidelines (not directly fetched; see L8)
  - https://hypixel.net/threads/what-is-actually-happening-with-the-eula-commercial-usage-guidelines.5452188/
- **Verification date:** 2026-10-25
- **Technical implications:**
  - "Ninja Launcher" is compliant.
  - "Ninja Minecraft Launcher", which is the repository folder name, puts the brand in the title. Avoid it as the product name.
  - The tagline "A launcher for Minecraft: Java & Bedrock Edition" is the allowed descriptive use.
  - The app icon and logo must not use Minecraft's grass block, creeper or the Minecraft font style.
- **Confidence:** Medium-high (tracked copy plus consistent secondary sources).
- **Limitations:** As in L8, the official page was not read directly.
- **Selected implementation approach:** Product name **Ninja Launcher**. Assembly and namespace `NinjaLauncher`. Use an original icon. Use a domain or repo name without "minecraft".

## L10. Minecraft Usage Guidelines: no game file redistribution

- **Description:** "No redistribution of games or altered game files" (CA-P-025265). Distribute the mod only, never a modded copy of the game (CA-P-025383). Mods must not include substantial copyrightable game code (CA-P-025379). All rights in the name and brand stay with Mojang and Microsoft (CA-P-025245).
- **Sources:**
  - https://conductatlas.com/platform/minecraft/minecraft-usage-guidelines/
  - https://www.minecraft.net/en-us/usage-guidelines
- **Verification date:** 2026-10-25
- **Technical implications:**
  - Never commit, bundle, mirror or cache-share client jars, assets, libraries, or Bedrock binaries. Our installers contain only our own code.
  - Files are downloaded at runtime from Mojang's official endpoints (piston-meta, resources.download.minecraft.net) onto the user's machine.
  - Do not host a CDN mirror. Do not offer "offline / cracked" accounts that skip ownership checks.
- **Confidence:** High.
- **Limitations:** The Mojang EULA (https://www.minecraft.net/en-us/eula) was not re-read this session.
- **Selected implementation approach:** CI checks that release artifacts contain no `*.jar` or Minecraft asset hashes. Launching requires a Microsoft account that owns the game. A demo mode, if added, must also come only from official endpoints.

## L11. Modrinth content and API

- **Description:**
  - **API rules:** Every request "must" send a uniquely identifying `User-Agent`, for example `github_username/project_name/1.56.0 (contact@launcher.com)`. The rate limit is 300 requests per minute per IP. Most reads need no token. Develop against `staging-api.modrinth.com` and use `api.modrinth.com` in production.
  - **Terms of Use (updated 2026-08-13):** The API grants a "limited, non-exclusive, non-sublicensable and revocable license to download, display, query..." Creators keep ownership of their content. The Terms also ban using site content to train AI models.
  - **Content licences:** Each project has its own licence. Modpack inclusion rules: files hosted on Modrinth may be used in Modrinth modpacks. Otherwise an open licence or explicit author permission is needed. "All Rights Reserved" generally forbids redistribution.
- **Sources:**
  - https://docs.modrinth.com/api/
  - https://modrinth.com/legal/terms
  - https://support.modrinth.com/en/articles/8797527-obtaining-modpack-permissions
- **Verification date:** 2026-10-25
- **Technical implications:**
  - The launcher must download mods directly from Modrinth's CDN on the user's behalf and must not re-host them.
  - If we export modpacks (`.mrpack`), reference Modrinth-hosted files by URL and hash instead of embedding the jars.
  - Show each project's licence in the UI.
- **Confidence:** High.
- **Limitations:** CurseForge, which needs an API key and has stricter third-party distribution rules, is out of scope here.
- **Selected implementation approach:**
  - Use `User-Agent: NinjaLauncher/<version> (+https://github.com/<owner>/ninja-launcher)`.
  - Add client-side throttling below 300 requests per minute and honour `X-Ratelimit-*` headers.
  - Download only from the URLs the API returns, and verify the SHA-512/SHA-1 hashes from the API.
  - Never embed third-party jars in exported packs.
