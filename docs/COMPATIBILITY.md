# Compatibility

## Launcher platforms

| Platform | Build | Tests run | Notes |
| --- | --- | --- | --- |
| Windows 11 x64 | ✅ | Unit, platform, integration, full install, launch smoke (2026-10-25) | Primary development machine |
| Linux x64 | CI configured | Not yet run | Secret storage needs a running Secret Service (GNOME Keyring or KWallet) and `secret-tool`. Without one, sign-ins last for the current session only |
| macOS arm64 | CI configured | Not yet run | Unsigned builds trigger Gatekeeper warnings (see B-03) |
| macOS x64 | CI configured (`macos-15-intel`) | Not yet run | GitHub plans to retire Intel runners in Fall 2027 |

## Minecraft Java Edition

| Range | Status |
| --- | --- |
| 1.13 → 26.x (modern `arguments`) | Supported. 1.21.1 was installed and booted (engine smoke). 26.x rules (`versionRange`, `default-user-jvm`) are covered by unit tests |
| 1.6 → 1.12.2 (legacy `minecraftArguments`, legacy natives) | Supported by the engine and unit-tested, not booted yet |
| Before 1.6 (`pre-1.6` resources) | Best effort |
| Apple Silicon or Windows ARM with LWJGL 2 versions (≤1.18) | Not supported: Mojang ships no native arm64 binaries for them |

## Mod loaders

| Loader | Minecraft versions | Status |
| --- | --- | --- |
| Fabric | Everything `meta.fabricmc.net` lists | Install verified live (1.21.1) |
| Quilt | Everything `meta.quiltmc.org` lists (beta loaders) | Install verified live (1.21.1) |
| NeoForge | 1.20.2+ and 26.x | Version list verified; installer run not yet verified |
| Forge | 1.12.2 and 1.13+ | Version list verified; installer run not yet verified. 1.7.10–1.12.1 are not supported |

## Bedrock Edition

| Platform | Status |
| --- | --- |
| Windows 10/11 | Detects the `Microsoft.MinecraftUWP` package and launches through `shell:AppsFolder`. Not yet verified on a real install |
| Linux / macOS | No official Bedrock. Ninja Launcher only launches an mcpelauncher you installed yourself (unofficial, requires the Android version from Google Play) |
