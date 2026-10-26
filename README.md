<p align="center"><img src="src/NinjaLauncher.Desktop/Assets/ninja-launcher.png" width="128" alt="Ninja Launcher logo"></p>

# Ninja Launcher

A free, open-source, cross-platform Minecraft launcher written in C# with .NET 10 and Avalonia UI.
It has a dark purple-and-pink look, runs each instance in its own folder, and talks only to official services.

> NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

## Status

Pre-release (v0.1.0 development). See [docs/ROADMAP.md](docs/ROADMAP.md) for each feature's status. Nothing here has been released yet.

| Area | Status |
| --- | --- |
| Application shell, SQLite, logging, settings, CI | Complete |
| Java Edition install engine (versions, libraries, assets, natives, Mojang Java runtimes) | Complete, verified against live Mojang servers |
| Java Edition launch | Partially complete: built and unit-tested. A real authenticated launch is blocked until Microsoft sign-in is approved (see below) |
| Microsoft sign-in (OAuth PKCE → Xbox → Minecraft) | Implemented; **blocked** until Mojang approves an Azure app ID |
| Instances (create, edit, clone, delete, repair, backups, disk usage) | Complete |
| Fabric, Quilt | Complete (install verified live) |
| Forge, NeoForge (official installers run headless) | Complete (installed and booted: NeoForge 21.1.265, Forge 47.4.28) |
| Modrinth search and install with dependencies | Complete (verified live) |
| CurseForge (search, install, modpacks) | Implemented with a bring-your-own API key, as CurseForge's terms require; not yet tested live |
| Modpacks (.mrpack install from Modrinth or file) | Complete (validation verified on a live pack); export not started |
| Installers (Velopack) and a signed update feed | Implemented; Windows packaging verified locally. Self-update needs the owner key (B-05) and a public repository |

## Microsoft sign-in and the app ID

Mojang only allows Azure application IDs it has reviewed to call Minecraft Services.
Ninja Launcher's own ID was approved on 2026-10-26 and is built in, so official builds need no setup.
If you build a fork, it must use its own approved ID; with any other ID, sign-in fails at `login_with_xbox` with HTTP 403.
To get one:

1. Register a free app in Microsoft Entra (personal Microsoft accounts, "Mobile and desktop" redirect URI `http://localhost`, no client secret). Don't put "Minecraft" in the app name.
2. Submit the app ID through the [AppID Review form](https://aka.ms/mce-reviewappid) ([details](https://help.minecraft.net/hc/en-us/articles/16254801392141)).
3. After approval, enter the ID under **Settings → Accounts**, or set the `NINJA_MSA_CLIENT_ID` environment variable.

Ninja Launcher never asks for your password, never uses another launcher's client ID, and has no offline or "cracked" mode.

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build NinjaLauncher.slnx
dotnet run --project src/NinjaLauncher.Desktop
dotnet test --project tests/NinjaLauncher.UnitTests
```

To publish a self-contained build:

```bash
dotnet publish src/NinjaLauncher.Desktop -c Release -r win-x64 --self-contained
```

The other runtime IDs are `linux-x64`, `osx-arm64` and `osx-x64`.

Data lives in `%APPDATA%\NinjaLauncher`, `~/Library/Application Support/NinjaLauncher` or `$XDG_DATA_HOME/NinjaLauncher`. Set `NINJA_LAUNCHER_HOME` to put it somewhere else, for example a portable folder.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Decisions](docs/DECISIONS.md)
- [Compatibility](docs/COMPATIBILITY.md)
- [Testing](docs/TESTING.md)
- [Security](SECURITY.md)
- [Threat model](docs/security/THREAT_MODEL.md)
- [Research notes](docs/research/), with sources and verification dates
- [Contributing](CONTRIBUTING.md)

## License

GPL-3.0-or-later. See [LICENSE](LICENSE). Third-party licences are listed in [docs/research/LICENSING.md](docs/research/LICENSING.md).
Minecraft is a trademark of Mojang Synergies AB. Ninja Launcher never downloads game files from anywhere except Mojang, and never ships them.
