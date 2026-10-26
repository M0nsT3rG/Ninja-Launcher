# Decisions

The authoritative list with evidence is in [docs/DECISIONS.md](../docs/DECISIONS.md). Quick recall:

- Own Java engine (no CmlLib), hand-written PKCE auth (no MSAL), and only the refresh token is stored, in OS storage.
- Sign-in stays disabled until a Mojang-approved Azure client ID is configured. No borrowed IDs, no offline mode.
- Fabric/Quilt install from meta profiles. Forge/NeoForge run their official installers headless after a SHA-1 check.
- Bedrock support was removed on 2026-10-26 at the owner's request (D-16); Java Edition only.
- Downloads check size on the fast path and do a full hash on repair.
- Tests use the Microsoft Testing Platform and xUnit v3. The full-install and launch-smoke tests are opt-in.
