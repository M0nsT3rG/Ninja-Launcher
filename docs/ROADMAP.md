# Roadmap

Statuses: COMPLETE, PARTIALLY COMPLETE, BLOCKED, NOT STARTED. "Verified live" means the test used real public servers, not mocks.

## Phase 0: Research (v0.1.0). COMPLETE
All seven research documents are written in `docs/research/`, each with sources, dates and confidence levels.

## Phase 1: Foundation (v0.1.0). COMPLETE
- Solution structure, DI, Serilog with redaction, SQLite with migrations, settings: COMPLETE
- Avalonia shell with 6 pages, dark and light themes, custom accent colour, owner-supplied logo: COMPLETE
- CI on Windows x64, Linux x64, macOS arm64 and macOS x64: COMPLETE (green on 2026-10-25)

## Phase 2: Authentication (v0.2.0). BLOCKED (external)
- PKCE loopback flow, state validation, Xbox, XSTS with error mapping, Minecraft profile, refresh with rotation, multiple accounts, switching and removal: implemented and unit-tested
- Real sign-in: **BLOCKED** until Mojang approves an Azure app ID (see `.ai/BLOCKERS.md` B-01). Verified live up to that point: browser PKCE, token exchange, Xbox Live and XSTS succeed, and Minecraft returns the expected 403
- Offline profiles (custom name, Prism-style): COMPLETE (unit-tested). Locked until a Microsoft account that owns Java Edition has signed in on this launcher, then stay unlocked. They use the vanilla offline UUID and the `legacy` user type, and never contact Microsoft. In practice they are unusable until B-01 is resolved

## Phase 3: Playable Java Edition (v0.3.0). PARTIALLY COMPLETE
- Version browsing (releases and snapshots), metadata parsing, inheritance, rules (including 26.x `versionRange`): COMPLETE
- Install of client, libraries, assets, legacy assets, natives and logging config, with hash checks, retry and repair: COMPLETE (verified live)
- Mojang Java runtime install, system Java fallback: COMPLETE (verified live)
- Argument generation (modern, legacy, `default-user-jvm`), process launch, log capture, exit status, cancellation: implemented and unit-tested
- **First playable acceptance test** (authenticated launch): BLOCKED by B-01

## Phase 4: Instance management (v0.4.0). PARTIALLY COMPLETE
- Create, rename, clone, delete (with confirmation), memory, JVM arguments, Java path, repair, world backup and restore, disk usage, open folder: COMPLETE
- Import and export of instance configurations (`.ninja.json`, no machine paths): COMPLETE
- Custom icons: NOT STARTED

## Phase 5: Modded (v0.5.0 / v0.6.0). PARTIALLY COMPLETE
- Fabric, Quilt: COMPLETE (verified live)
- Forge 1.12.2+, NeoForge 1.20.2+: COMPLETE (official installers run headless; NeoForge 21.1.265 and Forge 47.4.28 installed and booted)
- Modrinth search (mods, shaders, resource packs), compatibility filters, install with required dependencies, enable and disable: COMPLETE (verified live)
- Modpacks: browse on Discover, install from Modrinth, import a local .mrpack (host allow-list, sha1 + sha512, path containment, overrides): COMPLETE
- Mod updates (hash lookup via `/version_files/update`, works for hand-added jars, keeps the disabled state, release-only unless already on a pre-release): COMPLETE (verified live)
- Compatibility checks (Library + before Play: missing/disabled dependencies, incompatibilities, duplicates, wrong loader/version; one-click install of missing dependencies): COMPLETE (verified live and in the UI)
- CurseForge (search mods, modpacks, shaders and resource packs; install with dependencies; CurseForge modpack zips; author-blocked files sent to manual download): PARTIALLY COMPLETE. Implemented and unit-tested; live use needs the owner's API key (B-06)
- .mrpack export: NOT STARTED

## Phase 6: Bedrock (v0.7.0). PARTIALLY COMPLETE
- Windows detection and launch, data folder: implemented. **Not verified on a machine with Bedrock installed**
- Linux and macOS: detect an existing mcpelauncher only (unofficial, never bundled)

## Phase 7: Advanced (v0.8.0). PARTIALLY COMPLETE
- Crash-report viewer (crash-reports and JVM hs_err logs, summary with suspected mods and hints; failed exits report the reason): COMPLETE (unit-tested with sample reports, checked in the UI)
- Performance benchmarks, localisation, notifications: NOT STARTED

## Phase 8: Distribution (v0.9.0 → v1.0.0). PARTIALLY COMPLETE
- Velopack packaging (Windows Setup + portable zip, Linux AppImage, macOS .app/.pkg) and a draft-release workflow with SHA256SUMS: COMPLETE (Windows packaging verified locally; other platforms run only in the release workflow, **not yet run**)
- Signed update feed + in-app updater (opt-in install, startup check toggle): COMPLETE (unit-tested). BLOCKED for real use by B-05 (owner key)
- Linux .deb/Flatpak: NOT STARTED. Code signing and notarisation: optional, owner credentials (B-03)
