# Code review, 2026-10-25: mod updates and the signed updater (c3f04fb..0e5e55f)

An independent reviewer agent did this review; Codex CLI was at its usage limit. The reviewer checked Velopack's behaviour against the Velopack 1.2.161 source. It confirmed that `DownloadUpdatesAsync` verifies each package's size and SHA-256 against the feed, and that the bytes we verify are the same bytes that get parsed (no TOCTOU).

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| 1 | High | The signing key was a plain repository secret, so anyone with write access could get a build signed | The workflow runs on `v*` tags only (manual dispatch removed). Signing is a separate job in the protected `release` environment, with the owner as reviewer and a tag-only rule. Owner setup is documented in RELEASING.md |
| 2 | Medium | The update channel was derived from the OS architecture, which is wrong under emulation | The explicit channel was removed; Velopack uses the channel baked into the package |
| 3 | Medium | A newer pre-release hid release updates | The launcher falls back to the newest release for that project |
| 4 | Medium | An "update" could be a downgrade | An update is only offered if it is newer than the identified installed version |
| 5 | Medium | A stale update list could be applied after files changed | Each update re-verifies the old file's SHA-1 and is skipped if it changed. The list is also cleared on toggle and save |
| 6 | Low-med | The startup check showed errors when offline or with a private repo | The startup check only logs failures; "Check now" still reports them |
| 7 | Low | A race between the moving `latest` feed and its `.sig` | Signature verification is retried once, and the launcher re-checks right before downloading |
| 8 | Low | The key was present in the environment while the signer was being built | The signer is built in a separate step without the secret |
| 9 | Low | Shell injection through the `workflow_dispatch` input | Dispatch removed; the tag is regex-validated |
| 10 | Low | The draft release had no target commit | `--verify-tag` on the pushed tag |
| 11 | Low | Case-only renames left duplicate mods on Linux | Path comparison now follows the platform's case rules |
| 12 | Low | Updates ignored newly required dependencies | Dependencies are installed through the ledger-aware resolver |
| 13 | Low | keygen briefly wrote the private key world-readable | The file is created with `CreateNew` and mode 0600; Windows prints a note |
