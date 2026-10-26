# Releasing Ninja Launcher

Releases are built by `.github/workflows/release.yml` with [Velopack](https://velopack.io) 1.2.161 (MIT). The workflow only creates a **draft** GitHub release. Nothing reaches users until the owner publishes the draft.

## What each platform gets

| Channel | Artifacts |
| --- | --- |
| `win-x64` | `NinjaLauncher-win-x64-Setup.exe` (per-user installer, no admin), `NinjaLauncher-win-x64-Portable.zip`, full `.nupkg`, `releases.win-x64.json` + `.sig` |
| `linux-x64` | `NinjaLauncher.AppImage` (portable), full `.nupkg`, feed + `.sig` |
| `osx-arm64`, `osx-x64` | `.app` bundle in a `.pkg` and a portable zip, full `.nupkg`, feed + `.sig` |
| Linux packages | `ninja-launcher_<version>_amd64.deb`, `ninja-launcher-<version>-x86_64.flatpak` (no self-update; see `packaging/linux/README.md`) |
| all | `SHA256SUMS.txt` |

Not provided yet: an apt repository or Flathub listing, Authenticode signing, and Apple notarisation (see `.ai/BLOCKERS.md` B-03).

## How updates are authenticated

1. Velopack's feed `releases.<channel>.json` lists each package with its SHA-1, SHA-256 and size.
2. The release workflow signs every feed with the owner's **ECDSA P-256** key. The signed message is `"ninja-feed-v1\n" + channel + "\n" + feed bytes`, which ties each feed to its channel. The signature is written next to the feed as `releases.<channel>.json.sig`.
3. The app embeds only the public key (`branding/update-signing-key.pub.pem`). `SignedFeedSource` downloads the feed and its signature over HTTPS, refuses anything that does not verify, and then gives Velopack the verified feed. Velopack checks each downloaded package against the hashes in that feed.
4. Updates are never installed silently. The launcher can check at startup (the user can turn this off), but installing always needs a click on **Install and restart**.
5. A build without the public key has updates disabled. Development and non-installed runs also have updates disabled.

Known limitation: an attacker who controls the network can replay an older signed feed. That only hides newer versions ("freeze"); Velopack never downgrades.

## One-time setup (owner)

```bash
dotnet run --project tools/NinjaLauncher.FeedSigner -- keygen ninja-feed.key.pem branding/update-signing-key.pub.pem
```

1. Store `ninja-feed.key.pem` somewhere safe and offline, such as a password manager. **Never commit it**: `.gitignore` excludes `*.pem` except the public key.
2. Create a GitHub **Environment** named `release` (Settings → Environments). Add yourself as a **required reviewer**, and set deployment branches and tags to the tag pattern `v*`. Add the key's full PEM text there as the environment secret `NINJA_FEED_SIGNING_KEY`, **not** as a repository secret.
3. Add a tag ruleset (Settings → Rules → Rulesets) for `v*` so only you can create or move release tags.
4. Commit `branding/update-signing-key.pub.pem`. From then on, every build embeds it.

Why steps 2 and 3 matter: without them, anyone with write access to the repository (or a stolen token) could get a malicious build signed. With them, the private key is only exposed to the small `sign` job, and only after you approve that specific tagged run. The signer is compiled before the secret is set, and the release workflow can't be started by hand.
4. Self-update downloads from `https://github.com/M0nsT3rG/Ninja-Launcher/releases/latest/download/`. The repository is public (since 2026-10-25), so these downloads need no authentication.

If you lose the private key, users can't receive updates until they reinstall a build that has the new public key. Keep a backup.

## Pre-listed server (private)

Release builds can pre-list one server in every Java instance's Multiplayer list and in Bedrock's Servers tab. Its
details stay out of the repository: set the repository secret `LISTED_SERVER_JSON` to
`{"name": "<display name>", "javaAddress": "<host[:port]>", "bedrockHost": "<host>", "bedrockPort": 19132}` (either
address may be left out).
For local builds, put the same JSON in `src/NinjaLauncher.Desktop/listed-server.json` (git-ignored). Without either,
nothing is listed. Players can still see the address in-game, as with any server in their list.

## Built-in CurseForge key (private, off until permitted)

Release builds embed the repository secret `CURSEFORGE_API_KEY` so CurseForge works without setup; a key a user saves in
Settings takes precedence. Only add the secret once Overwolf has confirmed in writing that your key may be distributed
inside Ninja Launcher (CurseForge 3rd Party API Terms §2.2). For a local build, put the key in
`src/NinjaLauncher.Desktop/curseforge-api.key` (git-ignored by `*.key`).

## Cutting a release

1. Update `CHANGELOG.md` and the `<Version>` in `Directory.Build.props`.
2. `git tag v0.2.0 && git push origin v0.2.0`. Only tag pushes trigger the workflow; tags must look like `vMAJOR.MINOR.PATCH[-pre]`.
3. Approve the `sign` job when GitHub asks. Check that the run belongs to your tag.
4. Review the draft release and the `SHA256SUMS.txt`, then publish it. Publishing is the owner's decision.

## Local packaging (Windows example)

```bash
dotnet publish src/NinjaLauncher.Desktop -c Release -r win-x64 --self-contained -o publish
dotnet tool install -g vpk --version 1.2.161
vpk pack --packId NinjaLauncher --packVersion 0.1.0 --packDir publish --mainExe NinjaLauncher.exe --channel win-x64 --icon src/NinjaLauncher.Desktop/Assets/ninja-launcher.ico --delta None -o releases
```

This was verified on 2026-10-25: it produced Setup.exe, the portable zip, the nupkg and the feed, and the portable build launched.
