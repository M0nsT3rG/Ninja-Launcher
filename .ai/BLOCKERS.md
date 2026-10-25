# Blockers

## B-01: Microsoft and Minecraft sign-in needs an approved Azure app ID (owner action)
- **What's blocked:** `login_with_xbox` returns 403 "Invalid app registration" for any app ID that Mojang hasn't allow-listed.
- **Why:** Mojang reviews third-party API integrations by hand. Source: https://help.minecraft.net/hc/en-us/articles/16254801392141 (verified 2026-10-25).
- **What the owner needs to do:**
  1. In the Microsoft Entra admin center, create an app registration named "Ninja Launcher" for personal Microsoft accounts. Set a "Mobile and desktop applications" redirect to `http://localhost` and add no client secret.
  2. Submit the application (client) ID and directory (tenant) ID through https://aka.ms/mce-reviewappid.
  3. Once approved, enter the ID under Settings → Accounts, or set `NINJA_MSA_CLIENT_ID`.
- **Also blocked:** real sign-in, account refresh, and the first authenticated Java launch (the acceptance test).
- **Still possible meanwhile:** everything else. The install engine, loaders, Modrinth, instances and UI are all testable without an account.

## B-02: Bedrock on Windows has not been checked on a real install
Someone needs to run `Get-AppxPackage Microsoft.MinecraftUWP` and `Get-StartApps` on a PC that has Bedrock installed, to confirm the GDK app ID and launch behaviour.

## B-03: Code signing and notarisation (optional, owner-controlled, paid)
Apple Developer ID costs US$99 a year. Windows Authenticode certificates also cost money. Builds stay unsigned until the owner decides. See docs/research/CROSS_PLATFORM.md.

## B-04: The GitHub repository has not been created yet
Creating a private repository `Ninja-Launcher` under the authenticated account M0nsT3rG is allowed by the brief, but it has not been done yet. CI has not run on any OS until then.
