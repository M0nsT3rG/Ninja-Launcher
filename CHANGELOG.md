# Changelog

## [Unreleased] - v0.1.0 development

### Added
- .NET 10 / Avalonia 12 solution with a layered architecture, DI, SQLite and redacted Serilog logs.
- Dark and light purple-and-pink UI with custom accent colours and the owner-supplied logo. Pages: Home, Library, Discover, Downloads, Accounts, Settings.
- Java Edition engine: version manifest, inheritance, rule evaluation (including 26.x `versionRange`), hash-verified installation of client, libraries, assets and natives, Mojang Java runtimes, argument building (including `default-user-jvm`), process launch with log capture.
- Microsoft sign-in (PKCE, Xbox, XSTS, Minecraft Services) with OS-backed refresh-token storage. It is disabled until an approved app ID is configured.
- Isolated instances: create, edit, clone, delete, repair, world backup and restore, disk usage.
- Fabric and Quilt installs. Forge and NeoForge through their official installers.
- Modrinth search and install with required dependencies. Enable and disable mods.
- Bedrock detect-and-launch on Windows.
- CI for Windows, Linux, macOS arm64 and macOS x64.
