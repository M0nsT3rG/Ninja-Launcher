# Changelog

## [Unreleased] - v0.1.0 development

### Added
- An icon sidebar, a "Mods" page (formerly Discover) with filters for game version, mod loader and category, sorting, page size and a "Most followed" row, and a "Profiles" page (formerly Library) with cards, pinning and Java, Bedrock, Pinned, Custom and Modpack tabs. "Instances" are now called "profiles" everywhere.
- New Home: Java/Bedrock switch, your character in 3D drawn from your own skin, account picker, recent instances with Play, a custom card background, and the latest Minecraft news from minecraft.net.
- Bedrock Release and Preview on Home: Play opens the officially installed game, Install opens its Microsoft Store page. The partner server is pre-listed in Bedrock's Servers tab too.
- Wardrobe: change your Java Edition skin (classic or slim), reset it, and pick or hide any cape your account owns, with a live 3D preview.
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
- Discover tabs for mods, modpacks, resource packs, data packs and shaders, on Modrinth and CurseForge. Data packs install into the world you pick.
- Deleting an instance removes everything in it, CurseForge mods included, and an instance can't be changed or deleted while its game runs.
- Linux `.deb` and Flatpak packages, built and validated in CI and attached to tagged draft releases (`packaging/linux/README.md`).
