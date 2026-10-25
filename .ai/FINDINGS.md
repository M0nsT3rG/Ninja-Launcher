# Key findings (summary, with sources in docs/research)

- **Minecraft Services allow-list:** new Azure app IDs must be approved by Mojang through https://aka.ms/mce-reviewappid. Until then `login_with_xbox` returns 403. Source: help.minecraft.net article 16254801392141 (2026-10-25). → AUTHENTICATION.md
- **Microsoft endpoints:** `login.microsoftonline.com/consumers/oauth2/v2.0/{authorize,token}`, scope `XboxLive.signin offline_access`, loopback `http://localhost` (any port). Refresh tokens rotate and last 90 days. → AUTHENTICATION.md
- **Version manifest:** `piston-meta.mojang.com/mc/game/version_manifest_v2.json`. The latest release (2026-10-25) is 26.3, which needs Java 25 (`java-runtime-epsilon`). → JAVA_ENGINE.md
- **26.x format changes:** `arguments.default-user-jvm`, the `os.versionRange` rule, natives in `${natives_directory}/{java,jna,lwjgl,netty}`, no `--userType`. → JAVA_ENGINE.md J-03
- **Java runtime index:** `launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`. ARM platforms have no Java 8, 16 or 17 "beta". → J-12
- **Fabric/Quilt:** `/versions/loader/{mc}/{loader}/profile/json` returns an inheriting version JSON. → MOD_LOADERS.md
- **NeoForge versioning:** `21.1.x` = MC 1.21.1. From 26.1 the first three parts are the MC version. MC 1.20.1 lives under `net.neoforged:forge`. → L-05
- **Forge installers:** support `--installClient <dir>` from 1.12.2 and need a stub `launcher_profiles.json`. → L-08
- **Modrinth:** requires a unique User-Agent, allows 300 req/min, and `.mrpack` downloads are limited to an allow-list of hosts. → L-09…L-11
- **Bedrock on Windows:** a GDK app since 1.21.120. The package name stays `Microsoft.MinecraftUWP`, the app ID may be `Game`, and data lives in `%APPDATA%\Minecraft Bedrock`. → BEDROCK_FEASIBILITY.md
- **Avalonia 12:** compiled bindings are on by default, `Watermark` became `PlaceholderText`, and the old dialogs are gone (use `StorageProvider`). → CROSS_PLATFORM.md
- **.NET 10 SDK:** xUnit v3 projects need `global.json` → `"test": {"runner": "Microsoft.Testing.Platform"}`, run as `dotnet test --project …`.
