# Security policy

## Reporting

Please report vulnerabilities privately through GitHub Security Advisories on this repository. Don't open a public issue. We aim to reply within 7 days.

## What Ninja Launcher guarantees

- **No passwords.** Sign-in happens in your system browser (OAuth 2.0 authorization code + PKCE, with `state` checked). The launcher never sees your Microsoft password.
- **Tokens:** only the Microsoft refresh token is stored, in Windows DPAPI, the macOS Keychain or the Linux Secret Service. If none of these is available, nothing is written to disk. Tokens are redacted from logs, error messages and the logged command line.
- **Downloads:** HTTPS only, checked against the published SHA-1 and size, written to a temporary `.part` file and then moved into place. Retries are limited, and every request has a timeout and can be cancelled.
- **Archives:** extraction rejects path traversal and caps entry count and total size (decompression bombs).
- **Filesystem:** paths built from remote metadata or user input must stay inside their root. Deleting an instance removes only that instance's own folder, and only after you confirm.
- **Processes:** the game and loader installers start with an argument array, never through a shell.
- **No telemetry.** The launcher only contacts Microsoft/Xbox, Mojang, Modrinth and the mod loader sites you use.

## Things to keep in mind

- **Mods are code.** Anything installed from Modrinth or elsewhere runs with your user's permissions. Being listed in a catalog does not make a mod safe.
- **Forge and NeoForge installers** are Java programs from those projects' official Maven repositories. Ninja Launcher checks them against their published `.sha1` before running them.
- **Updates** are only accepted when the feed signature verifies against the embedded release public key, and each package is checked against the signed hashes. Updates never install without your click (docs/releases/RELEASING.md).

See [docs/security/THREAT_MODEL.md](docs/security/THREAT_MODEL.md) for the threat model.
