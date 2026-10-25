# Architecture

Ninja Launcher is a layered .NET 10 solution. The UI follows MVVM with CommunityToolkit.Mvvm, and all business logic lives outside the UI layer.

```
NinjaLauncher.Desktop            Avalonia 12 UI, view models, composition root (AppServices), job queue
   │
   ├── NinjaLauncher.Mods         Loader providers (Fabric/Quilt meta profiles, Forge/NeoForge installers), Modrinth client
   │     └── NinjaLauncher.Minecraft.Java   Version catalog, rule evaluator, inheritance merge, installer,
   │                                        Mojang Java runtimes, launch argument builder, game process
   ├── NinjaLauncher.Minecraft.Bedrock      Capability detection and launching of an installed Bedrock
   ├── NinjaLauncher.Infrastructure         SQLite repositories, HTTP downloader, Serilog, Microsoft auth
   ├── NinjaLauncher.Platform               OS paths, secret stores (DPAPI/Keychain/libsecret), Java discovery, URL/folder opening
   └── NinjaLauncher.Application            Interfaces, InstanceService, AccountService, security helpers
         └── NinjaLauncher.Domain           Records: GameInstance, MinecraftAccount, LauncherSettings
```

Dependencies only point downward. Minecraft edition providers depend only on `Application` abstractions such as `IDownloadService` and `IAppPaths`, never on Infrastructure.

## Data layout

```
<data>/ninja.db                 SQLite: instances, accounts (no secrets), settings, activity
<data>/logs/launcher-*.log      Rolling logs (7 days), redacted
<data>/secrets/*.bin            Windows only: DPAPI-encrypted refresh tokens
<data>/game/                    Shared, immutable, hash-verified content
    versions/<id>/<id>.json|.jar, natives-<arch>/
    libraries/…  assets/{indexes,objects,log_configs,virtual}/  runtimes/<component>/<platform>/
<data>/instances/<guid>/        One isolated game directory per instance (saves, mods, options, logs)
<data>/backups/<guid>/*.zip     World backups
<cache>/                        Manifest cache, loader installers
```

Each instance's game directory belongs to that instance alone. The shared `game/` tree only holds content-addressed or version-keyed files that no instance writes to.

## Launch pipeline

1. `VersionCatalog.ResolveAsync(id)`: load the version JSON (vanilla JSONs come from Mojang, SHA-1 checked) and flatten any `inheritsFrom` chain with `VersionMerger`.
2. `GameInstaller.InstallAsync`: resolve libraries for this OS (`LibraryResolver` + `RuleEvaluator`), queue the client jar, libraries, asset index, asset objects and log config, then download through `IDownloadService`, which checks size and SHA-1, retries, and writes atomically. Then extract legacy natives (`SafeArchive`) and lay out legacy or virtual assets.
3. `JavaRuntimeManager.EnsureRuntimeAsync`: install the Mojang runtime the version asks for (`javaVersion.component`). If Mojang has none for this platform, fall back to a matching system Java.
4. `LaunchArgumentBuilder.Build`: build the JVM and game arguments. It supports both the modern `arguments` and the legacy `minecraftArguments` format, plus `default-user-jvm`, `versionRange` and feature rules.
5. `GameProcess.RunAsync`: run the game with an argv array (no shell), unwrap log4j XML output, redact secrets, and report the exit code. Cancelling kills the process tree.

## Authentication

`MicrosoftAuthenticator` runs the authorization-code flow with PKCE in the system browser, using a loopback `HttpListener` and validating `state`. It then calls Xbox Live user authenticate, XSTS (`rp://api.minecraftservices.com/`), Minecraft `login_with_xbox`, and the profile endpoint. `AccountService` stores only the Microsoft refresh token, keeps the newest one after each rotation, and holds it in `ISecretStore`. The Minecraft token stays in memory.

## UI

`MainViewModel` owns navigation, toasts and a confirmation overlay. The pages are Home, Library, Discover, Downloads, Accounts and Settings. Long operations go through `JobQueue`, which shows them on the Downloads page with progress, speed, cancel and retry. All I/O runs on background threads, and the UI thread only receives posted updates.
