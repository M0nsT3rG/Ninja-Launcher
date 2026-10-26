# Changelog

## [Unreleased] - v0.1.0 development

### Removed
- Bedrock Edition support: detection, launch, the Microsoft Store link and the Servers tab pre-listing. Ninja Launcher is Java Edition only.

### Added
- .NET 10 / Avalonia 12 solution with a layered architecture, DI, SQLite and redacted Serilog logs.
- Dark and light purple-and-pink UI with custom accent colours and the owner-supplied logo. Pages: Home, Library, Discover, Downloads, Accounts, Settings.
- Java Edition engine: version manifest, inheritance, rule evaluation (including 26.x `versionRange`), hash-verified installation of client, libraries, assets and natives, Mojang Java runtimes, argument building (including `default-user-jvm`), process launch with log capture.
- Microsoft sign-in (PKCE, Xbox, XSTS, Minecraft Services) with OS-backed refresh-token storage. It is disabled until an approved app ID is configured.
- Isolated instances: create, edit, clone, delete, repair, world backup and restore, disk usage.
- Fabric and Quilt installs. Forge and NeoForge through their official installers.
- Modrinth search and install with required dependencies. Enable and disable mods.
- Modrinth mod update checks, Velopack packaging and a signed, opt-in self-update feed.
- Offline profiles with a custom name, unlocked after an owning Microsoft account signs in.
- Mod compatibility checks with one-click install of missing dependencies, and a crash-report viewer with plain-language hints.
- Project logos everywhere, custom instance icons, Modrinth and CurseForge modpack export, an Install Minecraft for Windows button that opens the Microsoft Store app and notices when the install finishes, and a Portuguese (Portugal) translation.
- CurseForge as a second source in Discover, plus CurseForge modpack import. Bring your own API key; files their authors block from other apps go to manual download.
- CI for Windows, Linux, macOS arm64 and macOS x64.
- Desktop notifications when a download finishes or the game closes or crashes while the launcher is in the background, and Portuguese for messages from the engine layers.
- Faster Library for big modpacks: only visible mod rows are created and Modrinth lookups are cached (300 mods: 2.1 s → 0.27 s, 290 → 217 MB).
- Linux `.deb` and Flatpak packages, built and validated in CI and attached to tagged draft releases (`packaging/linux/README.md`).
