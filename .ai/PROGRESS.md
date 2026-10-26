# Progress (for future sessions)

Last updated: 2026-10-25. See `docs/ROADMAP.md` for the status of each feature.

## Done in session 1 (2026-10-25)
- Research: `docs/research/*.md` (auth, Java engine, mod loaders, Bedrock, cross-platform, licensing), written by parallel research agents.
- Full solution scaffolded and building with zero warnings (TreatWarningsAsErrors).
- Java engine installs and boots 1.21.1 for real (full install + launch smoke tests).
- UI shell with 6 functional pages. Launched and checked by screenshot on Windows.
- 79 unit, 5 platform and 9 integration tests (live APIs).
- Owner logo integrated (`branding/ninja-launcher-logo.png` → `Assets/ninja-launcher.{png,ico}`).

## Waiting on external approvals (as of 2026-10-26)
- Mojang AppID review for client ID `db657a7e-8a69-4faa-ae7f-a607b8f9a30a` (B-01). When approved, sign in, then do the first real launch (the acceptance test).
- CurseForge API key application (B-06). When approved, the owner pastes the key in Settings → CurseForge, then run the live test with `NINJA_CURSEFORGE_API_KEY`.

## Next unblocked tasks
6. Run the release workflow once B-05 is done; then add a Linux .deb (nfpm) and Flatpak.
7. Fetch `.sha1` sidecars for hashless Fabric/Quilt libraries.

## How to verify quickly
- `dotnet test --project tests/NinjaLauncher.UnitTests`
- Live: `dotnet test --project tests/NinjaLauncher.IntegrationTests`
- Engine: set `NINJA_FULL_INSTALL=1`, `NINJA_LAUNCH_SMOKE=1` and `NINJA_IT_HOME=<dir>`, then run the integration tests.
- UI: `dotnet run --project src/NinjaLauncher.Desktop`. Set `NINJA_LAUNCHER_HOME` to use a scratch data folder.
