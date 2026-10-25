# Contributing

1. Install the .NET 10 SDK. Then run `dotnet build NinjaLauncher.slnx` and `dotnet test --project tests/NinjaLauncher.UnitTests`.
2. Branch from `main` (`feature/<topic>`) and keep `main` buildable.
3. Follow the layering in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). UI code must not contain business logic, and providers only depend on `NinjaLauncher.Application` abstractions.
4. Any path built from remote data goes through `SafePath`, and any archive goes through `SafeArchive`. Never log a token.
5. Add tests for anything non-trivial. Integration tests may call public APIs but must never use credentials.
6. Record significant decisions in `docs/DECISIONS.md` and research (with source URL and verification date) in `docs/research/`.
7. Don't contribute Minecraft game files, other launchers' code or assets, or anything you are not allowed to licence under GPL-3.0-or-later.

Commit messages should be short and imperative, e.g. `Add Quilt loader provider`.
