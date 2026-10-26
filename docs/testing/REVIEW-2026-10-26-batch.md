# Review — logos, instance icons, modpack export, pt-PT (2026-10-26)

Scope: commits `abbc1de..c470226` (project logos, custom instance icons, Bedrock Store link, `.mrpack` and
CurseForge export, Portuguese (Portugal) translation). Codex was at its usage limit, so the review was done by an
independent reviewer agent that was given the code, not our conclusions.

| # | Finding | Fix | Test |
|---|---------|-----|------|
| 1 | Remote/local images decoded without a size check (pixel bomb) | `RemoteImage.IsSafeImage` reads the header with `SKCodec`; max 4096×4096, aspect ≤ 4 | `HardeningTests.Normal_icons_pass_and_pixel_bombs_are_rejected` |
| 2 | A failed icon download during modpack import could fail the import / leave temp files | `TryAdoptIconAsync` swallows non-cancellation errors and deletes its temp file | — |
| 3 | Export could include credentials (e.g. ReAuth config), symlinked folders and `.git` | Shared `PackExportFiles.Collect`: skips links, hidden folders and private-looking names; the toast reports skipped files | `HardeningTests.Export_selection_skips_private_files_and_includes_pack_folders` |
| 4 | CurseForge fingerprint read whole files into memory | Two-pass streaming `CurseForgeFingerprint.OfFile` | `HardeningTests.Streaming_fingerprint_matches_in_memory_fingerprint` |
| 5 | CurseForge thumbnails kept in the memory cache (CurseForge terms: no caching) | Memory and disk cache only for Modrinth's CDN | — |
| 6 | Cancelling an export save dialog threw a null reference | Early return when the dialog is cancelled | — |
| 7 | Bedrock Store button shown when Bedrock was already installed | `CanGetBedrock` only when not installed | — |
| 8 | Unzipped resource/shader packs (folders) were dropped from exports | Sub-folders of content folders ship as overrides | covered by #3 test |
| 9 | Instance icon paths could point at UNC/other locations | Local images must be under the instances folder; UNC rejected | — |
| 10 | Corrupt cached icons stuck forever; cache never pruned | Corrupt entry deleted and refetched; files older than 30 days pruned at start | — |
| 11 | `StringFormat` texts bypassed translation | Converted to `LocalizeConverter`; `LocalizationTests` now checks `ConverterParameter` keys and flags literal `StringFormat`s | `LocalizationTests`, `HardeningTests.Localize_converter_formats_and_strips_the_xaml_brace_escape` |
| 12 | Default bitmap decoded per row; SHA-1 recomputed on every Library refresh | Default icon decoded once; SHA-1 cached by path/size/mtime | — |

Results: unit 178/178, platform 5/5, integration 12 passed / 5 opt-in skipped (includes the live `.mrpack`
export → import round trip).

Not verified: CurseForge export linking (needs an API key, blocker B-06). Existing installs keep the saved language
`en`; Portuguese is picked in Settings → Appearance.
