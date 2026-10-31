# Testing

```bash
dotnet test --project tests/NinjaLauncher.UnitTests          # fast, offline, no mocks of external APIs needed
dotnet test --project tests/NinjaLauncher.PlatformTests      # OS-specific behaviour (paths, secret store, Java discovery)
dotnet test --project tests/NinjaLauncher.IntegrationTests   # live public APIs: Mojang, Fabric, Quilt, NeoForge, Forge, Modrinth
```

| Kind | Project | Uses network | What it proves |
| --- | --- | --- | --- |
| Unit | UnitTests | No | Path and zip-slip safety, decompression limits, redaction, rule evaluation (including 26.x `versionRange`), inheritance merge, library resolution, argument building, log parsing, instance isolation, backup and restore, account flows (with a fake authenticator), loader version mapping |
| Platform | PlatformTests | No | OS detection, standard directories, Unicode paths, secret store round trip (DPAPI ciphertext is checked to not contain the plaintext), Java discovery |
| Integration (live) | IntegrationTests | Yes, no credentials | Manifest and metadata, Fabric/Quilt profile install and merge, NeoForge/Forge version lists, Modrinth search, version filters and dependency install, runtime index, hash-mismatch rejection, SQLite round trip and migrations |
| Full install (opt-in) | IntegrationTests `NINJA_FULL_INSTALL=1` | Yes (~1 GB) | A complete version and its Mojang Java runtime install with every file hash-verified; a second pass is a fast no-op |
| Engine launch smoke (opt-in) | IntegrationTests `NINJA_LAUNCH_SMOKE=1` with `NINJA_IT_HOME` | Yes | The generated command line boots the real client to LWJGL and resource initialisation, then the test stops it. It uses a placeholder session. **This is not an authenticated launch and not the acceptance test** |
| Live sign-in up to Mojang (manual) | manual | Yes | 2026-10-25 on Windows with a real personal account. Browser PKCE login, token exchange, Xbox Live and XSTS succeeded, and `login_with_xbox` returned the expected 403 (app ID not yet approved). The first attempt found a camelCase JSON bug (Xbox returned 400); it was fixed in 179560c |
| Live sign-in (manual) | manual | Yes | 2026-10-26 on Windows after Mojang approved the app ID: the full chain completed and the Minecraft profile was resolved |
| Authenticated launch (acceptance) | manual | Yes | **Passed 2026-10-31** on Windows: a new vanilla instance (26.4-snapshot-6) installed, launched with the owner's account (`Setting user: M0nsT3rG`, token redacted in the launcher log), reached the title screen with no session or profile-key errors, and exited with status 0 |

No test fixture contains real credentials or game files. The mocks are a fake `IMinecraftAuthenticator` and in-memory repositories, and they are only used for account and instance logic.

## Results log

| Date | Suite | Result |
| --- | --- | --- |
| 2026-10-25 | Unit | 79 passed |
| 2026-10-25 | Platform (Windows 11 x64) | 5 passed |
| 2026-10-25 | Integration (live) | 8 passed, 1 skipped (full install, opt-in) |
| 2026-10-25 | Full install 1.21.1 + java-runtime-delta (Windows x64) | passed, 72 s on a cold cache, ~970 MB |
| 2026-10-25 | Launch smoke 1.21.1 (Windows x64) | passed, client reached LWJGL init in ~16 s |
| 2026-10-25 | Loader installers (opt-in `NINJA_LOADER_INSTALL=1`) | NeoForge 21.1.265 and Forge 47.4.28 installed and resolved |
| 2026-10-25 | Launch smoke NeoForge 21.1.265, Forge 1.20.1-47.4.28 | first failed: the vanilla jar on the classpath clashed with NeoForge's patched module. Fixed by placing the client jar at `versions/<profile>/<profile>.jar`, as the official launcher does. Both now pass |
| 2026-10-25 | CI (Windows, Linux, macOS arm64, macOS x64) | green |
| 2026-10-25 | Independent code review (Codex was at its usage limit, so a separate reviewer agent ran) | 16 findings; all fixed with regression tests, see `docs/testing/REVIEW-2026-10-25.md` |
| 2026-10-25 | Unit / platform / integration after the review fixes | 93 / 5 / 13 passed (4 opt-in skipped) |
| 2026-10-25 | Launch smoke 1.8.9 (legacy, Java 8), NeoForge 21.1.265, 1.21.1 | all passed |
| 2026-10-25 | Mod updates (live: outdated Sodium detected and replaced, disabled state kept) | passed |
| 2026-10-25 | Compatibility (unit: 9 rules; live: Iris without Sodium flagged, dependency installed, re-check clean; UI driven via UI Automation) | passed |
| 2026-10-25 | Crash reports (unit: vanilla, NeoForge suspects, Fabric mixin, JVM GPU-driver crash, redaction) | passed; real game crash output not yet captured |
| 2026-10-26 | Logos, instance icons, exports and pt-PT (unit: icon validation and traversal, export loader mapping, MurmurHash2 reference vector, a localisation coverage test that fails on untranslated strings; live: `.mrpack` export → import round trip; UI: Discover logos, Library in Portuguese) | passed, 174 unit |
| 2026-10-25 | Codex review of compatibility + crash reports | 8 findings, all fixed (docs/testing/REVIEW-2026-10-25-compat.md); 136 unit and 15 integration tests pass |
| 2026-10-25 | Update feed signing (unit: valid, tampered, wrong channel, wrong key, missing .sig, http) + local vpk pack, portable build boots | 105 unit passed; Windows packaging verified |
