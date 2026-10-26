# Performance baseline (2026-10-26)

Machine: the owner's Windows 11 desktop. Release build (`dotnet build -c Release`), not self-contained/ReadyToRun.
Fixture: 30 instances; "Big pack" (Fabric 1.21.1) holds 300 mods — the 60 most-downloaded Fabric mods from Modrinth
plus 240 synthetic 2 MB jars (unknown to Modrinth). Driven by UI Automation; timings include UIA polling overhead.

| Measure | Before | After |
|---|---|---|
| Start → main window (warm, median of 3) | 1.35 s | 1.35 s |
| Start → main window (first run after a build, cold disk cache) | 11.2 s | 2.6 s* |
| CPU busy after the window appears | ~0.6 s | ~0.55 s |
| Memory at idle on Home (working set / private) | 159 MB / 131 MB | 160 MB / 131 MB |
| Library: select the 300-mod instance → first mod row | 2.1 s | 0.27 s |
| Library: select it again (hashes and Modrinth data cached) | 1.4 s | 0.12 s |
| Background work until idle (first time: SHA-1 of 618 MB, Modrinth lookups) | ~2.4 s | ~3.1 s |
| Background work, second time | 0.3–3.7 s (network again) | ~0.56 s (no network) |
| Memory with the 300-mod instance open (working set / private / peak) | 286 / 241 / 290 MB | 213 / 167 / 217 MB |

\* Not comparable: the "before" cold start was the first launch after a fresh build.

## Changes
- The mod list uses a `VirtualizingStackPanel`: only rows inside the visible part of the page are created (≈20
  instead of 300). Scrolling realises the rest; checked at 15/50/85 % with screenshots.
- Modrinth project names and logos are remembered per file hash for the session, including "not on Modrinth", so
  switching back to an instance makes no network calls. SHA-1s were already cached by path, size and mtime.

## Not measured
Linux and macOS, HDDs, launches of the game itself (bound by Java and Minecraft), Discover search latency
(network-bound). Reproduce with the scripts described in `docs/TESTING.md` (UIA scripts are not in the repository).
