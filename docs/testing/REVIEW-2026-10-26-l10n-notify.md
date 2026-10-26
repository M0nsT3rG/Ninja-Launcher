# Review — engine translation, notifications, Linux packages, performance (2026-10-26)

Scope: `664e438..94a2199`. Codex was at its usage limit; an independent reviewer agent read the code (no conclusions
given). No high-severity findings.

| # | Finding | Outcome |
|---|---------|---------|
| 1 | Nested translation ran filled-in names through short templates ("Play Online" → "Jogar Online") | Fixed: only a value the key puts after ": " (an embedded message) is translated; test added |
| 2 | Flatpak lacked permission for the notification service; .deb did not recommend notify-send | Fixed: `--talk-name=org.freedesktop.Notifications`, `Recommends: libnotify-bin`. notify-send inside the Flatpak runtime is unverified |
| 3 | Windows notifications stopped for the session after Explorer restarts | Fixed: a failed update re-adds the icon |
| 4 | One regex timeout skipped every remaining template | Fixed: timeouts are per template |
| 5 | "Packing" and "Updates are not available." untranslated; Desktop progress/exception literals not scanned | Fixed: translated, and the engine scan now also covers Desktop `Report(new(` / `throw new` lines |
| 6 | notify-send could read text starting with "-" as an option | Fixed: `--` before the title |
| 7 | Flatpak builder image and action used moving tags in the release pipeline | Fixed: pinned by digest / commit SHA |
| 8 | No notification when Play failed before the game started | Fixed: Play failures notify through the job; a clean exit notifies from the game session |
| 9 | `.desktop` has no `StartupWMClass` | Open: needs checking on a Linux desktop (`xprop WM_CLASS`) |

Results after fixes: unit 180/180, platform 5/5, integration 12 passed / 5 opt-in skipped; Windows notification call
accepted (`Shell_NotifyIcon` returned true); CI built and test-installed the .deb and built the Flatpak.
