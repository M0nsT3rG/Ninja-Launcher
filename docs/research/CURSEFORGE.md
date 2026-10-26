# CurseForge integration research

Verified 2026-10-26. Sources: https://docs.curseforge.com/rest-api/, and the CurseForge 3rd Party API Terms and Conditions at https://support.curseforge.com/en/support/solutions/articles/9000207405.

## C-01: API access needs a personal, non-shareable key
- **Description:** Every request to `https://api.curseforge.com` needs the `x-api-key` header. Third-party modding services apply for a key through the CurseForge for Studios console (console.curseforge.com) and the application form linked from the docs. Overwolf reviews each application, weighing its impact on author earnings and on CurseForge's servers and CDN. Terms §2.2: the key "is non-transferable and may not be shared with any third party". §9: delete all copies when access ends.
- **Technical implications:** The key can't go in this public repository or in a binary distributed to other people. Prism Launcher ships one under its own agreement with Overwolf; we don't have such an agreement.
- **Confidence:** High for §2.2. Whether Overwolf would allow distributing a key inside a GPL launcher can only be settled by asking them.
- **Selected approach:** **Bring your own key, plus an optional built-in key.** Settings → CurseForge takes the user's key and stores it in the OS secret store (DPAPI, Keychain or libsecret). It is never written to logs or files. Release builds can also embed the owner's key from the `CURSEFORGE_API_KEY` repository secret (`Services/BuiltInKeys.cs`); a user's own key takes precedence. The secret stays unset until Overwolf permits distributing the key in this build. Without any key, CurseForge stays disabled and the UI explains why. See blocker B-06.

## C-02: No caching, no hiding behind a VPN or proxy, and a competition clause
- **Description:** §3.1 says a developer may not "save or cache any data obtained through the API or SDK", may not "conceal your identity or geographic location when accessing the API" (including through a VPN or proxy), and may not build a product that "competes, directly or indirectly, with CF". §2.3 lets Overwolf impose quotas; there's no published number.
- **Technical implications:** The CurseForge client keeps nothing from the API: no in-memory cache and nothing on disk. Downloaded mod files aren't API data. The key holder must not use the API through a VPN. Overwolf decides whether a launcher counts as competing; Prism, ATLauncher and others were approved.
- **Confidence:** High for the wording. Medium for how Overwolf applies it.
- **Selected approach:** Every request goes to the API directly, page sizes stay small (20), and the Settings page warns about the VPN rule.

## C-03: Authors can block third-party downloads
- **Description:** The Mod object has `allowModDistribution`. When an author turns third-party distribution off, the file's `downloadUrl` is null and the file can only be downloaded on curseforge.com. This toggle is how CurseForge protects author revenue (Overwolf blog: https://medium.com/overwolf/the-curseforge-official-api-is-now-live-d314606355c2).
- **Technical implications:** Building CDN links (`edge.forgecdn.net/files/…`) by hand would bypass the author's choice. The brief forbids that ("Do not bypass restrictions on third-party mod distribution").
- **Confidence:** High.
- **Selected approach:** The launcher only downloads files that have a non-null `downloadUrl` from `*.forgecdn.net` over HTTPS, with SHA-1 verification. Blocked files are listed with their curseforge.com page so the user can download them, and the launcher opens the instance's mods folder.

## C-04: Endpoints and enums used
- Search: `GET /v1/mods/search?gameId=432&classId=&searchFilter=&gameVersion=&modLoaderType=&sortField=2&sortOrder=desc&index=&pageSize=`. Responses are wrapped in `data` and `pagination{index,pageSize,resultCount,totalCount}`, and `index + pageSize` can be at most 10,000.
- Mods `POST /v1/mods {modIds}`; files `GET /v1/mods/{id}/files?gameVersion=&modLoaderType=`; batch files `POST /v1/mods/files {fileIds}`.
- `ModLoaderType`: 0 Any, 1 Forge, 2 Cauldron, 3 LiteLoader, 4 Fabric, 5 Quilt, 6 NeoForge (from the docs).
- File: `hashes[{value, algo}]` (algo 1 is SHA-1, 2 is MD5), `downloadUrl`, `dependencies[{modId, relationType}]` (1 EmbeddedLibrary, 2 Optional, 3 Required, 4 Tool, 5 Incompatible, 6 Include), `gameVersions`, `isAvailable`, `releaseType`, `fileLength`, `fileDate`.
- Minecraft `gameId` = 432. Classes: mods 6, modpacks 4471, resource packs 12 (community clients agree). The shaders class is looked up at runtime by slug (`shaders`) through `GET /v1/categories?gameId=432&classesOnly=true`, because I couldn't confirm a fixed ID.
- **Confidence:** High for the documented enums. Medium for the class IDs and for `sortField=2` meaning Popularity, because the ModsSearchSortField table wasn't in the parts of the docs I could read.

## C-05: CurseForge modpack format
- A ZIP containing `manifest.json` with `{minecraft:{version, modLoaders:[{id:"forge-47.2.0"|"neoforge-…"|"fabric-…", primary}]}, files:[{projectID, fileID, required}], overrides:"overrides"}`. There's no official spec; community launchers agree on this layout.
- **Selected approach:** Import a local zip, or install from Discover. Files are resolved in one `POST /v1/mods/files` call plus `POST /v1/mods` for `allowModDistribution`. Allowed files are downloaded and checked. Blocked files are reported for manual download. Overrides go through `SafeArchive`.

## Not done (yet)
- Fingerprint-based identification (MurmurHash2) for updates and compatibility of CurseForge-sourced mods. The Modrinth checker still covers mods that are also on Modrinth.
- Watching the Downloads folder to pick up manually downloaded blocked files, which is how Prism does it.
