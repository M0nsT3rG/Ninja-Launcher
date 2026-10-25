# Codex review, 2026-10-25: compatibility checks and crash reports (8e7d7b6)

Codex CLI reviewed this commit (read-only). All 8 findings were valid, and each was fixed with a regression test where one could be written.

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| 1 | P1 | Play could launch a different instance than the one checked if the selection changed during the check | Home and Library now capture the instance before awaiting |
| 2 | P1 | A finished dependency install refreshed another instance's lists | The refresh only runs if that instance is still selected |
| 3 | P2 | Version-pinned and version-only dependencies were ignored | Version-only dependencies are resolved through `/versions?ids=`. A different installed version of a pinned dependency is a warning (authors often pin one exact build). Incompatibilities with a specific version only match that version |
| 4 | P2 | "Install missing" dropped the pinned version | The pin is carried in `ModIssue.MissingVersionId` and installed exactly |
| 5 | P2 | The 8-second pre-launch timeout did not stop file hashing | Hashing now uses cancellable `SHA1.HashDataAsync` |
| 6 | P2 | Stale compatibility results stayed actionable after mods changed | Results are cleared on toggle and update, and a content revision drops in-flight results. The install action re-checks before installing |
| 7 | P2 | An exception with no message was skipped and an unrelated line reported | The parser takes the first non-blank line after `Description:`, so the colon is no longer required |
| 8 | P2 | Crash-report I/O errors hid the real exit status | The crash description is best effort and falls back to the exit-status message |
