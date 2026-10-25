# Progress (for future sessions)

Last updated: 2026-10-25. See `docs/ROADMAP.md` for the status of each feature.

## Done in session 1 (2026-10-25)
- Research: `docs/research/*.md` (auth, Java engine, mod loaders, Bedrock, cross-platform, licensing), written by parallel research agents.
- Full solution scaffolded and building with zero warnings (TreatWarningsAsErrors).
- Java engine installs and boots 1.21.1 for real (full install + launch smoke tests).
- UI shell with 6 functional pages. Launched and checked by screenshot on Windows.
- 79 unit, 5 platform and 9 integration tests (live APIs).
- Owner logo integrated (`branding/ninja-launcher-logo.png` → `Assets/ninja-launcher.{png,ico}`).

## Next unblocked tasks
1. `.mrpack` export (import is done).
2. Run the NeoForge/Forge installers end to end in an integration test (opt-in, they are heavy).
3. Mod update checks via `/version_files` hashes, and dependency warnings.
4. Instance import/export (config JSON) and custom icons.
5. Crash-report viewer (read `crash-reports/` in the instance directory).
6. Velopack packaging and a signed release feed (Phase 8).
7. Fetch `.sha1` sidecars for hashless Fabric/Quilt libraries.

## How to verify quickly
- `dotnet test --project tests/NinjaLauncher.UnitTests`
- Live: `dotnet test --project tests/NinjaLauncher.IntegrationTests`
- Engine: set `NINJA_FULL_INSTALL=1`, `NINJA_LAUNCH_SMOKE=1` and `NINJA_IT_HOME=<dir>`, then run the integration tests.
- UI: `dotnet run --project src/NinjaLauncher.Desktop`. Set `NINJA_LAUNCHER_HOME` to use a scratch data folder.
