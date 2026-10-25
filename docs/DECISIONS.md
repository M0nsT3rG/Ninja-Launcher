# Decisions

Each decision lists the evidence it rests on. Research notes are in `docs/research/`.

| # | Date | Decision | Why | Evidence |
| --- | --- | --- | --- | --- |
| D-01 | 2026-10-25 | Original implementation, C# / .NET 10 / Avalonia 12.1.4 / CommunityToolkit.Mvvm 8.4.2 | Locked by the project brief. Avalonia 12 is stable and MIT-licensed | CROSS_PLATFORM.md |
| D-02 | 2026-10-25 | Write our own Java Edition engine instead of using CmlLib.Core | CmlLib (MIT) lacks the 26.x `versionRange` and `default-user-jvm` handling, hardcodes hosts, and pulls in LZMA-SDK and SharpZipLib. Our own engine keeps control of hashing, retries, paths and redaction. It may still serve as a test oracle | JAVA_ENGINE.md J-14, J-15 |
| D-03 | 2026-10-25 | Hand-written OAuth code + PKCE flow, not MSAL.NET | MSAL hides the refresh token, so we would have to serialise its cache into our own secret store. The PKCE loopback flow (RFC 7636/8252) is short and easy to audit. **Revisit** if Microsoft changes the flow | AUTHENTICATION.md |
| D-04 | 2026-10-25 | Store only the Microsoft refresh token, in OS storage: DPAPI, Keychain via `security -i` and hex, or libsecret via stdin. With no backend, keep it in memory only | No plaintext fallback, and the secret never appears in process arguments | AUTHENTICATION.md §6 |
| D-05 | 2026-10-25 | Sign-in is disabled until an approved Azure client ID is configured (settings or `NINJA_MSA_CLIENT_ID`) | Mojang allow-lists app IDs. No other launcher's ID is borrowed | help.minecraft.net article 16254801392141 |
| D-06 | 2026-10-25 | Downloads skip files whose size already matches; "Repair" re-hashes everything | Hashing about 600 MB on every launch is slow; repair stays thorough | JAVA_ENGINE.md J-08 |
| D-07 | 2026-10-25 | Fabric and Quilt install from meta profile JSON. Forge (1.12.2+) and NeoForge (1.20.2+) run their official installers headless after a `.sha1` check | Forge and NeoForge patch the game jar with Java processors | MOD_LOADERS.md L-04…L-08 |
| D-08 | 2026-10-25 | Bedrock is detect-and-launch only: `Get-AppxPackage`/`Get-StartApps` and `shell:AppsFolder\<AUMID>` on Windows; an externally installed mcpelauncher elsewhere | No licence bypass, no unpacking, no bundled binaries | BEDROCK_FEASIBILITY.md |
| D-09 | 2026-10-25 | SQLite stores instance, account and settings records as JSON in keyed tables, versioned with `PRAGMA user_version` | A small schema that can still evolve | — |
| D-10 | 2026-10-25 | Microsoft Testing Platform with xUnit v3; tests split into unit, platform and integration (live APIs) | The .NET 10 SDK drops VSTest for MTP projects | build output |
| D-11 | 2026-10-25 | Logo is the owner-supplied "Ninja Launcher Logo no BG.png" (in `branding/`) | Provided by the owner | — |
| D-12 | 2026-10-25 | Use the product name "Ninja Launcher"; never put "Minecraft" in the product name | Minecraft usage guidelines | LICENSING.md |
| D-14 | 2026-10-25 | Offline profiles are gated on verified ownership (one successful Microsoft sign-in), as in Prism Launcher. There is no ungated offline mode | The brief forbids cracked-account authentication and bypassing ownership. Owners still get offline/LAN play. The unlock flag lives in its own settings row so settings saves cannot clear it | AccountService |
| D-13 | 2026-10-25 | Updater: Velopack 1.2.161 with a channel-bound ECDSA P-256 signature on each feed (`SignedFeedSource`); packages are checked by Velopack against the verified feed hashes. Never installs silently | ECDSA P-256 is built into .NET, so no BouncyCastle dependency is needed for Ed25519; it is equally strong for this purpose. Velopack on its own only checks hashes from an unauthenticated feed | CROSS_PLATFORM.md, docs/releases/RELEASING.md |
