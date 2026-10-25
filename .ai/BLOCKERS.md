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

## B-05: Update signing key (owner action)
Self-update stays disabled until the owner does three things (see docs/releases/RELEASING.md): generate the key with `tools/NinjaLauncher.FeedSigner keygen`, add the private key as the repository secret `NINJA_FEED_SIGNING_KEY`, and commit `branding/update-signing-key.pub.pem`. Updates are also downloaded from GitHub Releases, so they only work once the repository is public (B-04).

## B-04: Going public (owner decision)
The private repository https://github.com/M0nsT3rG/Ninja-Launcher was created on 2026-10-25. Making it public, or publishing releases, needs the owner's explicit approval. Before that, audit it for secrets, licences and proprietary assets.
