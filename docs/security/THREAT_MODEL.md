# Threat model

| Asset | Threat | Mitigation | Where |
| --- | --- | --- | --- |
| Microsoft refresh token | Theft from disk or logs | OS secret storage only, no plaintext fallback, redaction everywhere | `SecretStores.cs`, `SecretRedactor`, `RedactingFormatter` |
| OAuth flow | Code interception, CSRF | PKCE S256, random `state` checked, loopback redirect, 5-minute timeout | `MicrosoftAuthenticator` |
| Game files | Tampered download or CDN/MITM attack | HTTPS only, SHA-1 and size checks, atomic move | `HttpDownloadService` |
| Launcher filesystem | Malicious metadata paths (`../`), zip-slip | `SafePath.Combine` on every remote path, checked extraction | `SafePath`, `SafeArchive`, `LibraryResolver`, `GameInstaller`, runtime links |
| User disk | Decompression bomb | Entry count and byte caps (512 MB for natives) | `SafeArchive` |
| Instance data | Accidental deletion of unrelated folders | Instance directories are GUID-named under the instances root; deletion needs confirmation and goes through `SafePath` | `InstanceService` |
| Process execution | Argument injection | `ProcessStartInfo.ArgumentList`, no shell; user JVM arguments are split, never evaluated by a shell | `GameProcess`, `LaunchArgumentBuilder` |
| Mods | Malicious mod | Shown as a warning before every install; only downloads from `cdn.modrinth.com`; hash checked | `ModrinthClient`, Discover page |
| Loader installers | Tampered installer | `.sha1` sidecar checked; official Maven hosts only; 15-minute timeout | `InstallerLoaderProvider` |
| macOS Keychain | Secret visible in the process list | Passed as hex to `security -i` over stdin | `MacKeychainSecretStore` |

## Residual risks

- SHA-1 is the only hash Mojang publishes for game files. That is weak against a determined attacker but it is what's available, and HTTPS still protects the transport.
- Some Fabric/Quilt libraries ship without hashes in their profile. They are fetched over HTTPS from the official Maven repositories. Planned: fetch the `.sha1` sidecar.
- Keychain items trust `/usr/bin/security`, so any process running as the same user can read them. This is documented in AUTHENTICATION.md.
