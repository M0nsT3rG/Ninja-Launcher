# Roadmap

Statuses: COMPLETE, PARTIALLY COMPLETE, BLOCKED, NOT STARTED. "Verified live" means the test used real public servers, not mocks.

## Phase 0: Research (v0.1.0). COMPLETE
All seven research documents are written in `docs/research/`, each with sources, dates and confidence levels.

## Phase 1: Foundation (v0.1.0). COMPLETE
- Solution structure, DI, Serilog with redaction, SQLite with migrations, settings: COMPLETE
- Avalonia shell with 6 pages, dark and light themes, custom accent colour, owner-supplied logo: COMPLETE
- CI on Windows x64, Linux x64, macOS arm64 and macOS x64: COMPLETE (green on 2026-10-25)

## Phase 2: Authentication (v0.2.0). COMPLETE
- PKCE loopback flow, state validation, Xbox, XSTS with error mapping, Minecraft profile, refresh with rotation, multiple accounts, switching and removal: implemented and unit-tested
- Real sign-in: COMPLETE (verified live 2026-10-26). Mojang approved the app ID, which is now built in (Settings or `NINJA_MSA_CLIENT_ID` can override it)
- Offline profiles (custom name, Prism-style): COMPLETE (unit-tested). Locked until a Microsoft account that owns Java Edition has signed in on this launcher, then stay unlocked. They use the vanilla offline UUID and the `legacy` user type, and never contact Microsoft. In practice they are unusable until B-01 is resolved

## Phase 3: Playable Java Edition (v0.3.0). PARTIALLY COMPLETE
- Version browsing (releases and snapshots), metadata parsing, inheritance, rules (including 26.x `versionRange`): COMPLETE
- Install of client, libraries, assets, legacy assets, natives and logging config, with hash checks, retry and repair: COMPLETE (verified live)
- Mojang Java runtime install, system Java fallback: COMPLETE (verified live)
- Argument generation (modern, legacy, `default-user-jvm`), process launch, log capture, exit status, cancellation: implemented and unit-tested
- **First playable acceptance test** (authenticated launch): COMPLETE (passed 2026-10-31 on Windows with the owner's account)

## Phase 4: Instance management (v0.4.0). PARTIALLY COMPLETE
- Create, rename, clone, delete (with confirmation), memory, JVM arguments, Java path, repair, world backup and restore, disk usage, open folder: COMPLETE
- Import and export of instance configurations (`.ninja.json`, no machine paths): COMPLETE
- Custom instance icons (PNG/JPG/WebP/GIF ≤ 2 MB; Modrinth modpacks adopt the pack icon): COMPLETE
- Pre-list a partner server in Multiplayer (details kept out of the repository, see docs/releases/RELEASING.md): COMPLETE
  - Java: added once to every instance's `servers.dat` before launch, existing servers kept byte for byte, never re-added after the player deletes it: COMPLETE (unit-tested; inactive until the private config exists; not yet seen in-game because sign-in is blocked by B-01)

## Phase 5: Modded (v0.5.0 / v0.6.0). PARTIALLY COMPLETE
- Fabric, Quilt: COMPLETE (verified live)
- Forge 1.12.2+, NeoForge 1.20.2+: COMPLETE (official installers run headless; NeoForge 21.1.265 and Forge 47.4.28 installed and booted)
- Modrinth search (mods, shaders, resource packs), compatibility filters, install with required dependencies, enable and disable: COMPLETE (verified live)
- Modpacks: browse on Discover, install from Modrinth, import a local .mrpack (host allow-list, sha1 + sha512, path containment, overrides): COMPLETE
- Mod updates (hash lookup via `/version_files/update`, works for hand-added jars, keeps the disabled state, release-only unless already on a pre-release): COMPLETE (verified live)
- Compatibility checks (Library + before Play: missing/disabled dependencies, incompatibilities, duplicates, wrong loader/version; one-click install of missing dependencies): COMPLETE (verified live and in the UI)
- CurseForge (search mods, modpacks, shaders and resource packs; install with dependencies; CurseForge modpack zips; author-blocked files sent to manual download): PARTIALLY COMPLETE. Implemented and unit-tested; live use needs the owner's API key (B-06)
- Modpack export: Modrinth `.mrpack` (verified live with a round trip) and CurseForge `.zip` (fingerprint matching is unverified until a key is available; without a key everything is bundled): COMPLETE
- Project logos in Discover and on installed mods: COMPLETE
- Discover tabs for mods, modpacks, resource packs, data packs and shaders on both sources; data packs install into a chosen world's `datapacks` folder: COMPLETE (Modrinth verified live; CurseForge data packs use the `data-packs` class looked up by slug, unverified until a key is available)

## Phase 6: Bedrock (v0.7.0). COMPLETE (detect and launch)
- Removed on 2026-10-26 (D-16), brought back on 2026-11-01 at the owner's request (D-17)
- Home's Bedrock tab lists Release and Preview: Play opens the officially installed game through Windows, Install opens its Microsoft Store page and notices when the install finishes. The protected package is never read or changed: COMPLETE (Release detected and opened live; Preview detection by package name, not installed here)
- Pre-listing the partner server in Bedrock's Servers tab (Release and Preview profiles): COMPLETE (unit-tested; inactive until the private config has a Bedrock host)

## Home and Wardrobe (2026-11-01). COMPLETE
- Home: Java/Bedrock switch, the signed-in account drawn in 3D from its own skin (drag to turn, no rendering service), account picker, the three most recent instances with Play, custom card background (PNG/JPEG, 16 MB, 4096 px), and the official launcher news feed linking to minecraft.net articles: COMPLETE (verified live)
- Wardrobe: upload a 64×64 skin (classic or slim arms), reset to the default skin, pick or hide any cape the account owns, live 3D preview with a turn-around button: COMPLETE (reading skin and capes, and switching the cape, verified live on 2026-11-01: the public profile showed the newly chosen cape; the owner then saved the slim model, reset to the default skin and re-uploaded the original, which Mojang served byte-identical)

## Sidebar, Mods and Profiles redesign (2026-11-01). COMPLETE
- Icon rail sidebar with tooltips and screen-reader names, Settings and a play-latest button at the bottom; the Minecraft disclaimer moved to a footer that stays in view: COMPLETE (verified live)
- "Discover" renamed "Mods": large kind tabs, a filters panel (your profile and "only compatible", game version, mod loader, categories), sort order, results per page, first/previous/next/last page, a "Most followed" row, and result rows with downloads, followers and last update: COMPLETE (verified live on both sources on 2026-11-01 with the owner's approved CurseForge key: results, logos, the Popular row and category filtering; installing from CurseForge not yet exercised)
- "Library" renamed "Profiles" and "instance" renamed "profile" everywhere, in English and Portuguese. Profiles are a card grid with Java, Bedrock, Pinned, Custom and Modpack tabs and counts, pin/unpin, Play now, and the existing editor behind the settings button. Profiles installed from a modpack are recorded as such: COMPLETE (grid verified live)

## Phase 7: Advanced (v0.8.0). PARTIALLY COMPLETE
- Crash-report viewer (crash-reports and JVM hs_err logs, summary with suspected mods and hints; failed exits report the reason): COMPLETE (unit-tested with sample reports, checked in the UI)
- Localisation: Portuguese (Portugal), 430+ strings including engine messages (errors, progress, mod issues, crash hints, activity), checked by tests; the sign-in browser page stays English: COMPLETE
- Desktop notifications (download finished/failed, game closed/crashed; only while the launcher is in the background; setting to turn off): COMPLETE. Windows call verified; Linux/macOS commands not run on a desktop yet
- Performance baseline and fixes (virtualised mod list, cached Modrinth lookups; see docs/testing/PERFORMANCE.md): COMPLETE on Windows

## Phase 8: Distribution (v0.9.0 → v1.0.0). PARTIALLY COMPLETE
- Velopack packaging (Windows Setup + portable zip, Linux AppImage, macOS .app/.pkg) and a draft-release workflow with SHA256SUMS: COMPLETE (Windows packaging verified locally; other platforms run only in the release workflow, **not yet run**)
- Signed update feed + in-app updater (opt-in install, startup check toggle): COMPLETE (unit-tested). BLOCKED for real use by B-05 (owner key)
- Linux .deb (amd64) and Flatpak (org.freedesktop.Platform 26.08) built from the self-contained publish, validated in CI (`dpkg-deb`, `desktop-file-validate`, `appstreamcli`, test install of the .deb, Flatpak build) and attached to the draft release: PARTIALLY COMPLETE. Not yet run in CI or launched on a Linux desktop. Updates come from the package manager (the in-app updater stays off), and the Flatpak keeps sign-ins for one session only because the runtime has no `secret-tool` (`packaging/linux/README.md`)
- Code signing and notarisation: optional, owner credentials (B-03)
