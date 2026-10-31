# Blockers

## B-01: Microsoft and Minecraft sign-in needs an approved Azure app ID (RESOLVED 2026-10-26)
- **Resolution:** Mojang approved client ID `db657a7e-8a69-4faa-ae7f-a607b8f9a30a` on 2026-10-26. It is built in as the default (`BuiltInKeys.MicrosoftClientId`); `NINJA_MSA_CLIENT_ID` or an ID saved in Settings still wins. The owner signed in live the same day (profile resolved), and the first authenticated game launch passed on 2026-10-31.

- **What's blocked:** `login_with_xbox` returns 403 "Invalid app registration" for any app ID that Mojang hasn't allow-listed.
- **Why:** Mojang reviews third-party API integrations by hand. Source: https://help.minecraft.net/hc/en-us/articles/16254801392141 (verified 2026-10-25).
- **What the owner needs to do:**
  1. In the Microsoft Entra admin center, create an app registration named "Ninja Launcher" for personal Microsoft accounts. Set a "Mobile and desktop applications" redirect to `http://localhost` and add no client secret.
  2. Submit the application (client) ID and directory (tenant) ID through https://aka.ms/mce-reviewappid.
  3. Once approved, enter the ID under Settings → Accounts, or set `NINJA_MSA_CLIENT_ID`.
- **Status 2026-10-25:** Azure app registered (client ID `db657a7e-8a69-4faa-ae7f-a607b8f9a30a`, tenant `63df642b-61f6-4564-abd5-80db75d8abfc`, personal accounts, redirect `http://localhost`). The owner submitted the AppID review form (confirmed 2026-10-26); the launcher is waiting for Mojang's approval. A live test on 2026-10-25 showed every step works up to `login_with_xbox`, which returns 403 as expected until approval.
- **Also blocked:** real sign-in, account refresh, and the first authenticated Java launch (the acceptance test).
- **Still possible meanwhile:** everything else. The install engine, loaders, Modrinth, instances and UI are all testable without an account.

## B-03: Code signing and notarisation (optional, owner-controlled, paid)
Apple Developer ID costs US$99 a year. Windows Authenticode certificates also cost money. Builds stay unsigned until the owner decides. See docs/research/CROSS_PLATFORM.md.

## B-05: Update signing key (owner action)
Self-update stays disabled until the owner does three things (see docs/releases/RELEASING.md): generate the key with `tools/NinjaLauncher.FeedSigner keygen`, create the protected `release` environment (owner as required reviewer, `v*` tags only), add the private key there as `NINJA_FEED_SIGNING_KEY`, add a `v*` tag ruleset, and commit `branding/update-signing-key.pub.pem`. The repository is public, so GitHub Releases downloads work without authentication.

## B-06: CurseForge API key (owner action)
- **What's blocked:** CurseForge search, installs and modpacks only work with a personal API key. The CurseForge 3rd Party API Terms §2.2 make keys "non-transferable and may not be shared with any third party", so Ninja Launcher can't bundle one for everyone.
- **Owner action:** apply at https://console.curseforge.com (third-party application form). Overwolf reviews applications by hand. Once you have a key, paste it into Settings → CurseForge; it is stored in the OS secret store. Don't use the API through a VPN (§3.1).
- **To offer CurseForge to every user (owner's choice, 2026-10-26):** the built-in key is implemented but off. Release builds embed the repository secret `CURSEFORGE_API_KEY` when it exists; a key a user saves in Settings still wins. **Set that secret only after Overwolf confirms in writing that the key may ship inside this open-source build** (Prism Launcher has such an agreement), because §2.2 forbids sharing a key otherwise and anyone can extract a key from a distributed app.
- **Status 2026-10-26:** the owner submitted the CurseForge API application. Waiting for Overwolf's review.
- **Still possible meanwhile:** everything is implemented and unit-tested. The live test runs with `NINJA_CURSEFORGE_API_KEY`.

## B-04: Repository visibility (RESOLVED 2026-10-25)
https://github.com/M0nsT3rG/Ninja-Launcher was made public on 2026-10-25 at the owner's request. The pre-publication audit found no secrets in the tree or history, no game files, a GPL-3.0 licence and the Mojang disclaimer. The owner chose to keep their Gmail address as the commit author. Publishing a release is still the owner's decision.
