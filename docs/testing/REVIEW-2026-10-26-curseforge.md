# Codex review, 2026-10-26: CurseForge integration (a71ef44)

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| 1 | High | Automatic redirects could forward `x-api-key` to another host, or download from outside the CDN | New `RedirectGuardHandler`: the API client only reaches `api.curseforge.com`, and downloads only `*.forgecdn.net`. Every hop is HTTPS-checked, and custom headers are never forwarded to another host |
| 2 | High | Files without a SHA-1 were downloaded unverified | A valid 40-hex SHA-1 is required, otherwise the file is refused |
| 3 | High | An existing file with the right size was trusted without hashing | `DownloadItem.VerifyExisting` re-hashes existing CurseForge files |
| 4 | Medium | Discover reused search objects, including old distribution permissions | The project is re-fetched right before installing, and CurseForge results are re-queried each time the page is shown |
| 5 | Medium | A required dependency missing from the batch lookup was silently skipped | Install fails and names the missing project IDs |
| 6 | Medium | The 64-project limit silently dropped dependencies | It now fails with a clear message |
| 7 | Medium | Blocked-file report truncated at 8 | The full list is shown in a scrollable dialog and saved to `manual-downloads.txt`; only the automatically opened tabs are capped at 8 |
