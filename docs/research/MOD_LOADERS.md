# Mod loaders, Modrinth and .mrpack research

Project: Ninja Launcher (C#/.NET 10 + Avalonia, GPL-3.0-or-later)
Verification date for every finding: **2026-10-25**
Method: live HTTP calls to the meta and Maven endpoints. The NeoForge 21.1.265 and 26.3.0.99 installers and the Forge 26.3-66.1.0, 1.12.2-14.23.5.2859 and 1.7.10-10.13.4.1614 installers were downloaded **for inspection only**: their JSON and class-file strings were read, and nothing was executed. The Modrinth OpenAPI spec (`docs.modrinth.com/openapi.yaml`, v2.7.0) and the .mrpack help article were read.

Each finding uses the same fields: Description, Source, Verified, Technical implications, Confidence, Limitations, Selected approach.

Version JSON merge rules (`inheritsFrom`) are in JAVA_ENGINE.md J-07.

---

## 1. Fabric

### L-01 Fabric meta v2
- **Description:** Base `https://meta.fabricmc.net/v2`. Endpoints:
  - `/versions/game` returns `[{version, stable}]`. It includes snapshots, e.g. `26.4-snapshot-5`.
  - `/versions/loader` returns `[{separator, build, maven, version, stable}]`. Latest stable is **0.20.1**.
  - `/versions/loader/{game}` returns `[{loader, intermediary, launcherMeta{version, min_java_version, libraries{client, common, server}, mainClass}}]`.
  - `/versions/loader/{game}/{loader}/profile/json` returns a **ready-to-use version JSON** that has `inheritsFrom`.
  - `/profile/zip` (HTTP 200) is the same thing zipped for the vanilla launcher.
  - `/versions/intermediary/{game}`.
  - `/v2` alone returns 404 ("Endpoint GET /v2 not found").
  Live `.../loader/26.3/0.20.1/profile/json`: `id: fabric-loader-0.20.1-26.3`, `inheritsFrom: 26.3`, `mainClass: net.fabricmc.loader.impl.launch.knot.KnotClient`, `arguments{game:[], jvm:["-DFabricMcEmu= net.minecraft.client.main.Main "]}`. Libraries: asm 9.10.1 ×5, `sponge-mixin 0.17.5+mixin.0.8.7`, `fabric-loader 0.20.1`, each as `{name, url:"https://maven.fabricmc.net/", md5?, sha1?, sha256?, sha512?, size?}`.
  **26.x has no intermediary:** `/versions/intermediary/26.3` returns `net.fabricmc:intermediary:0.0.0`, and the 26.3 profile has no intermediary library, because the game is unobfuscated. The 1.21.1 profile does contain `net.fabricmc:intermediary:1.21.1`.
  **Hashes are missing** for `fabric-loader` and `intermediary` entries, while the asm/mixin entries have them.
- **Source:** https://meta.fabricmc.net/v2/versions/game ; https://meta.fabricmc.net/v2/versions/loader ; https://meta.fabricmc.net/v2/versions/loader/26.3 ; https://meta.fabricmc.net/v2/versions/loader/26.3/0.20.1/profile/json ; https://meta.fabricmc.net/v2/versions/loader/1.21.1/0.20.1/profile/json
- **Verified:** 2026-10-25.
- **Technical implications:** Installing Fabric is pure data: fetch the profile, save it as `versions/<id>/<id>.json`, resolve libraries as `url + mavenPath(name)`, and merge with vanilla. No Java process is needed at install time. For libraries without a hash, fetch `<jar>.sha1` (HEAD on `fabric-loader-0.20.1.jar.sha1` returned 200).
- **Confidence:** High.
- **Limitations:** `releaseTime`/`time` in the profile are the *request time*, not fixed, so don't use them for cache validation.
- **Selected approach:** `FabricInstaller` = GET the profile JSON → write it → let the vanilla engine do the rest. Default to the newest `stable:true` loader, and show only game versions that `/versions/loader/{game}` returns non-empty for.

## 2. Quilt

### L-02 Quilt meta v3
- **Description:** Base `https://meta.quiltmc.org/v3`. It mirrors Fabric: `/versions/game`, `/versions/loader` (entries include `file_size` and `hashes{sha1, sha256, sha512}`), `/versions/loader/{game}`, `/versions/loader/{game}/{loader}/profile/json`. The latest loader listed is **0.31.0-beta.4**, a beta; Quilt loader releases are all `-beta.N`.
  Profile for 1.21.1: `mainClass: org.quiltmc.loader.impl.launch.knot.KnotClient`, `arguments{game:[]}`. Libraries are `sponge-mixin 0.17.4`, quilt-json5, asm ×5, quilt-config, quilt-loader, `org.quiltmc:hashed:1.21.1` and `net.fabricmc:intermediary:1.21.1`, with `url` set to `https://maven.quiltmc.org/repository/release/` or `https://maven.fabricmc.net/`, and **no hashes**. The 26.3 profile works (HTTP 200) and contains no hashed/intermediary libraries.
  Ordering quirk: `/versions/loader/26.3` listed `0.20.0-beta.9` first, so don't assume index 0 is the newest.
- **Source:** https://meta.quiltmc.org/v3/versions/loader ; https://meta.quiltmc.org/v3/versions/game ; https://meta.quiltmc.org/v3/versions/loader/1.21.1/0.31.0-beta.4/profile/json ; https://meta.quiltmc.org/v3/versions/loader/26.3/0.31.0-beta.4/profile/json
- **Verified:** 2026-10-25.
- **Technical implications:** We can reuse the Fabric installer code with a different base URL. Sort loader versions semantically ourselves.
- **Confidence:** High.
- **Limitations:** We did not assess Quilt's project health or ecosystem size.
- **Selected approach:** A `MetaProfileInstaller(baseUrl)` shared by Fabric (`/v2`) and Quilt (`/v3`). Sort loaders with a SemVer comparer that understands `-beta.N`.

## 3. NeoForge

### L-03 Versions and the version-to-Minecraft mapping
- **Description:** `https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml`: `<latest>`/`<release>` = **26.3.0.99**. There is also a JSON API: `https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge` returns `{isSnapshot, versions[]}`, and `/api/maven/latest/version/releases/net/neoforged/neoforge?filter=<prefix>` returns `{version}`. The filter is a **plain string prefix**: `filter=21.1` returned `21.11.48`, so use `21.1.`.
  Mapping:
  - Before 26: `A.B.x` means MC `1.A.B`, and `1.A` when B is 0 (e.g. `21.1.265` → 1.21.1, `20.4.x` → 1.20.4).
  - From 26.1 on: four components. The first three are the MC version, with hotfix `.0` meaning none. Example from the official post: `26.1.0.10-beta` = MC 26.1, NeoForge build 10. `26.1.2.124` = MC 26.1.2.
  - MC 1.20.1 is a separate artifact, `net.neoforged:forge` (versions `1.20.1-47.1.7` … `1.20.1-47.1.106`, 63 versions).
  - Odd entries exist: `0.25w14craftmine.*-beta` (April Fools) and `26.1.0.0-alpha.N+snapshot-M`.
- **Source:** https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml ; https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge ; https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml ; https://neoforged.net/news/26.1release/ ("NeoForge versions now have four components…")
- **Verified:** 2026-10-25.
- **Technical implications:** We can't get the MC version from string concatenation alone. We need a mapper that branches on major version < 26. Mark `-beta`/`-alpha` versions as unstable. The authoritative MC version is `install_profile.json.minecraft` inside the installer.
- **Confidence:** High.
- **Limitations:** No official JSON maps NeoForge versions to MC versions. The mapping is a convention we derive.
- **Selected approach:** List versions from the Maven JSON API and map them with the rule above. Before installing, confirm against `install_profile.minecraft` and refuse if the two disagree.

### L-04 NeoForge installer contents (install_profile.json and processors)
- **Description:** Installer URL: `https://maven.neoforged.net/releases/net/neoforged/neoforge/<v>/neoforge-<v>-installer.jar` (with a `.sha1` sidecar).
  - **21.1.265** (7.0 MB): `install_profile.json` has keys `spec:1`, `profile`, `version`, `minecraft:"1.21.1"`, `json:"/version.json"`, `mirrorList`, `data{MAPPINGS, MOJMAPS, MERGED_MAPPINGS, BINPATCH, MC_UNPACKED, MC_SLIM, MC_EXTRA, MC_SRG, PATCHED, MCP_VERSION}`, `processors[]`, `libraries[]` and `serverJarPath`. The client-relevant processors are `installertools` (`MCP_DATA`, `DOWNLOAD_MOJMAPS`, `MERGE_MAPPING`), `jarsplitter`, `AutoRenamingTool` and `binarypatcher`. Processors marked server-only (`sides:["server"]`) are skipped for the client.
  - `version.json` contains `id: neoforge-21.1.265`, `inheritsFrom: 1.21.1` and `mainClass: cpw.mods.bootstraplauncher.BootstrapLauncher`. Its game args are `--fml.neoForgeVersion … --launchTarget forgeclient`. Its JVM args use `-p ${library_directory}/…${classpath_separator}…`, `--add-modules ALL-MODULE-PATH`, `-DlibraryDirectory=${library_directory}` and `-DignoreList=client-extra,${version_name}.jar`.
  - **26.3.0.99** (3.7 MB): this is much simpler. `data{BINPATCH, PATCHED, MCP_VERSION}`, and one shared processor, `installertools:4.0.17:fatjar --task PROCESS_MINECRAFT_JAR … --apply-patches`. `mainClass: net.neoforged.fml.startup.Client`.
- **Source:** the two installer jars above (read with a zip reader, not executed)
- **Verified:** 2026-10-25.
- **Technical implications:** NeoForge **requires running Java processors** on the user's machine to produce the patched client jar, so it can't be installed from data alone. The processors need: the vanilla client jar, the libraries listed in `install_profile.libraries`, substitution of `{TOKEN}` (from `data`, with client/server values) and `[maven coords]`, the `{MINECRAFT_JAR}`, `{SIDE}`, `{ROOT}`, `{INSTALLER}` and `{LIBRARY_DIR}` tokens, and execution via each processor jar's `Main-Class` with a classpath. Outputs can be checked against the `outputs` hashes.
- **Confidence:** High.
- **Limitations:** We did not verify the full token list (`{MINECRAFT_VERSION}`, `{LIBRARY_DIR}`, etc.) against the installer source.
- **Selected approach:** See L-05.

### L-05 Running the official installer headless: supported?
- **Description:** The NeoForge installer (`Implementation-Version: 3.0.48`, LegacyInstaller fork, `Main-Class: net.minecraftforge.installer.SimpleInstaller`) contains the CLI options `install-client`, `install-server`, `installClient`/`installServer` (aliases), `offline`, `debug`, `help`, `mirror` and `fat-installer*`. The help text in the class says "Install a client to the specified directory, defaulting to the MC installation directory". `ClientInstall` **requires `launcher_profiles.json` (or `launcher_profiles_microsoft_store.json`) in the target directory**; otherwise it fails with "There is no minecraft launcher profile … you need to run the launcher first!". It then "injects" a profile into that file. Licence of the installer repo (neoforged/LegacyInstaller): LGPL-2.1, last push 2026-10-17.
- **Source:** class-file strings in `neoforge-21.1.265-installer.jar` and `neoforge-26.3.0.99-installer.jar` ; GitHub API for https://github.com/neoforged/LegacyInstaller
- **Verified:** 2026-10-25.
- **Technical implications:** `java -jar neoforge-<v>-installer.jar --install-client <dir>` is an **officially exposed CLI option**, so it is supported. Nothing found restricts automated use. It needs a Java runtime: use the Mojang runtime for the target MC version. To reuse our cache, point `<dir>` at our shared root (the one containing `libraries/` and `versions/`) after pre-placing `versions/<mc>/<mc>.jar` and `.json`, so the installer finds the jar instead of downloading it. Write a stub `launcher_profiles.json` = `{"profiles":{}}`. After it finishes, read `versions/neoforge-<v>/neoforge-<v>.json`.
- **Confidence:** High that the flag exists and works headless. Medium on exactly which already-present files it reuses.
- **Limitations:** The installer writes into the target directory as it likes and is a black box for progress reporting (we only get stdout lines). We did not test it end-to-end in this research pass.
- **Selected approach:** **MVP: run the official installer headless** with `--install-client <sharedRoot>`, a stub `launcher_profiles.json` (created if absent, cleaned afterwards), the Mojang Java of the target version, stdout streamed to the log, a timeout and cancellation. **Later:** a native processor runner (parse `install_profile`, run processors ourselves) for better progress reporting and no profile injection. Prism and CmlLib.Core.Installer.NeoForge take that route.

## 4. Forge

### L-06 Version discovery
- **Description:** `https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json` returns `{homepage, promos{"<mc>-latest"|"<mc>-recommended": "<forgeVersion>"}}`, with 118 keys (e.g. `26.3-recommended: 66.1.0`, `1.21.1-recommended: 52.1.20`, `1.20.1-recommended: 47.4.28`, `1.12.2-recommended: 14.23.5.2859`, `1.7.10-recommended: 10.13.4.1614`). `https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json` returns `{"<mc>": ["<full maven version>", …]}`. `https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml` has 5,094 versions. Full Maven versions are usually `<mc>-<forge>`, but old ones carry suffixes (e.g. `1.7.10-10.13.4.1614-1.7.10`, `1.7.10_pre4-…`).
- **Source:** the three URLs above
- **Verified:** 2026-10-25.
- **Technical implications:** Always resolve the full coordinate from `maven-metadata.json`. Don't build it as `mc + "-" + forge`.
- **Confidence:** High.
- **Limitations:** —
- **Selected approach:** Use `maven-metadata.json` to list versions per MC version, and promotions to mark Latest/Recommended.

### L-07 Installer formats across eras
- **Description:**
  - **Modern (1.13+, e.g. 26.3-66.1.0):** `spec:1`, `json:/version.json`, 5 processors (`installertools:1.4.6` ×3 server-only, then `binarypatcher:1.3.4` once for server and once for client). It bundles `maven/.../forge-<v>-shim.jar`, `-universal.jar` and `data/client.lzma`. `mainClass: net.minecraftforge.bootstrap.ForgeBootstrap`. The jar is signed (`META-INF/FORGE.SF`).
  - **1.12.2-14.23.5.2859:** `spec:0`, `json:/version.json`, **0 processors**. `mainClass: net.minecraft.launchwrapper.Launch`. Installing is data-only plus libraries.
  - **Old (1.7.10-10.13.4.1614):** `install_profile.json` = `{install{profileName, target, path, filePath: "forge-…-universal.jar", minecraft, mirrorList}, versionInfo{id: 1.7.10-Forge10.13.4.1614-1.7.10, inheritsFrom: 1.7.10, mainClass: net.minecraft.launchwrapper.Launch, minecraftArguments: "… --tweakClass cpw.mods.fml.common.launcher.FMLTweaker", libraries}}`. The universal jar inside the installer must be copied to `libraries/<path>`.
  - **≤1.5.2:** `forge-1.5.2-7.8.1.738-universal.zip` exists (HEAD 200). Releases before 1.6 are "jar mods" patched into `minecraft.jar`.
  - **CLI flags (from class strings):** modern and 1.12.2 installers have `installClient`, `installServer`, `offline`, `debug`, `mirror` and `help`. The help text reads "Install the client files to the specified directory". The **1.7.10 installer only has `installServer`**: no headless client install. Forge's `ClientInstall` also requires `launcher_profiles.json` in the target, the same as NeoForge.
- **Source:** installer jars at `https://maven.minecraftforge.net/net/minecraftforge/forge/<v>/forge-<v>-installer.jar` for the three versions listed (read, not executed) ; Forge LICENSE.txt in the 26.3 installer (LGPL 2.1) ; GitHub API for https://github.com/MinecraftForge/Installer (LGPL-2.1, branch 2.0, last push 2026-10-03)
- **Verified:** 2026-10-25.
- **Technical implications:** Three code paths: (a) `spec ≥ 0` with processors → official installer `--installClient <dir>` (or a native processor runner); (b) `spec 0` without processors → we could just extract `version.json` and download libraries, though the installer works as well; (c) legacy `versionInfo` → extract `versionInfo` as the version JSON and copy the universal jar. Pre-1.6 jar-mods would need jar patching.
- **Confidence:** High.
- **Limitations:** Old Forge libraries (Scala, typesafe) on 1.7.10 use the `url`/`checksums`/`clientreq` fields and some had `.pack.xz`. This was not re-verified live, so mirror availability for 1.7.10-era libraries is unknown.
- **Selected approach:** Support Forge **≥1.12.2** in the MVP through the official installer headless (`java -jar forge-<v>-installer.jar --installClient <sharedRoot>`, stub `launcher_profiles.json`). Support 1.7.10–1.11.x legacy `versionInfo` extraction in a later milestone. Don't support pre-1.6 jar-mods.

### L-08 Forge ads and automated download policy
- **Description:** The Forge download page links every file through `https://adfoc.us/serve/sitelinks/?id=271228&url=https://maven.minecraftforge.net/…`. The page text says: "Ads are Forge's main source of income … If you'd like to support Forge while keeping ads blocked, please consider supporting LexManos on Patreon." The page text we fetched contains **no terms of service, and no clause forbidding direct Maven downloads by tools**. `maven.minecraftforge.net` serves installers directly (HTTP 200, `application/java-archive`), and the code is LGPL-2.1.
- **Source:** https://files.minecraftforge.net/net/minecraftforge/forge/ (HTML fetched and scanned) ; HEAD on the maven.minecraftforge.net installer URLs
- **Verified:** 2026-10-25.
- **Technical implications:** Direct Maven downloads are technically and licence-wise possible, and are what major open-source launchers do. Ethically, we bypass Forge's ad revenue.
- **Confidence:** Medium. The *absence* of a restriction was verified only on the downloads page, not on every Forge legal page. No separate Forge ToS URL was found.
- **Limitations:** Forge may publish a policy later. This is not legal advice.
- **Selected approach:** Download installers directly from `maven.minecraftforge.net` and verify them with the `.sha1` sidecar. Show a visible "Forge is ad-supported. Support Forge" link (to the files page or Patreon) on the Forge install dialog. Re-check the policy before each release.

---

## 5. Modrinth API v2

### L-09 Base URL, User-Agent, rate limit
- **Description:** Base `https://api.modrinth.com/v2` (the root `https://api.modrinth.com/` returns 301). Spec version: `v2.7.0`. **User-Agent is mandatory** and must identify the application uniquely. The spec says "you **must** use provide a uniquely-identifying `User-Agent` header" and gives as best format `github_username/project_name/1.56.0 (contact@launcher.com)`; library-only agents such as `okhttp/4.9.3` "increase the likelihood that we will block your traffic". **Rate limit: 300 requests/minute per IP**, the same with or without a token, reported in the `X-Ratelimit-Limit`, `X-Ratelimit-Remaining` and `X-Ratelimit-Reset` headers (the live response showed `X-Ratelimit-Limit: 300`). Deprecated API versions eventually return **410**.
- **Source:** https://docs.modrinth.com/openapi.yaml (sections "Ratelimits", "User Agents", "Versioning") ; live response headers from `/v2/search`
- **Verified:** 2026-10-25.
- **Technical implications:** One shared `HttpClient` with a fixed UA, e.g. `<github-user>/ninja-launcher/<version> (<contact or repo URL>)`. A client-side limiter (token bucket of about 300 per minute). Honour 429 with `X-Ratelimit-Reset`. Treat 410 as "update the launcher".
- **Confidence:** High.
- **Limitations:** The contact detail in the UA is recommended, not required. Don't put the user's email in it.
- **Selected approach:** A `ModrinthClient` with a `DelegatingHandler` that adds the UA and enforces the limit. Use batch endpoints (`/projects?ids=`, `/version_files`) to save requests.

### L-10 Endpoints used by the launcher
- **Description:**
  - `GET /search?query=&facets=&index=relevance|downloads|follows|newest|updated&offset=&limit=(≤100)` returns `{hits[], offset, limit, total_hits}`. `facets` is a JSON string: an outer array means AND, an inner array means OR, e.g. `[["categories:fabric"],["versions:1.21.1"],["project_type:mod"]]`. Operators are `:`/`=`, `!=`, `>=`, `>`, `<=` and `<`. Loaders are filtered through `categories`. **New `environment` facet and field** (values like `client_only`, `client_and_server`, `server_only`, …). `client_side`/`server_side` are marked deprecated. Also `disclosure_types` (e.g. `ai_content`, `advertisements`, `telemetry`).
  - `GET /project/{id|slug}` returns the project (keys include `id`, `slug`, `project_type`, `loaders`, `game_versions`, `versions`, `environment`, `client_side`, `server_side`, `license`, `icon_url`, `body`, …).
  - `GET /project/{id|slug}/version?loaders=["fabric"]&game_versions=["1.21.1"]&featured=&include_changelog=false`. Both filters are JSON arrays in the query string. Live, it returned 22 Sodium versions. Each version has `files[{hashes{sha1,sha512}, url (cdn.modrinth.com), filename, primary, size, file_type}]` and `dependencies[]`.
  - `GET /version_file/{hash}?algorithm=sha1|sha512` (live: Sodium's sha1 resolved to version `SMxNOGZ6`). `POST /version_files {hashes[], algorithm}`. `POST /version_file/{hash}/update` and `POST /version_files/update` (latest version for given loaders/game_versions).
  - `GET /project/{id}/dependencies`, `GET /versions?ids=`, `GET /tag/loader` (live list includes fabric, quilt, forge, neoforge, liteloader, ornithe, legacy-fabric, babric, …).
  - **Dependencies:** `{version_id?, project_id?, file_name?, dependency_type}` with `dependency_type` ∈ `required`, `optional`, `incompatible`, `embedded`.
  - IDs are 8-character base62. Slugs can change, so store IDs.
- **Source:** https://docs.modrinth.com/openapi.yaml ; live calls to `/v2/search`, `/v2/project/sodium`, `/v2/project/sodium/version?...`, `/v2/version_file/003c114c…?algorithm=sha1`, `POST /v2/version_files`, `/v2/tag/loader`
- **Verified:** 2026-10-25.
- **Technical implications:** The dependency resolver follows `required` recursively, offers `optional`, blocks `incompatible`, and ignores `embedded` (it ships inside the jar). Hash lookups let us identify mods the user added by hand. Always send `include_changelog=false` for listings.
- **Confidence:** High.
- **Limitations:** The `environment` model is new. Older clients that only read `client_side`/`server_side` still work for now, since both fields exist.
- **Selected approach:** Typed DTOs (System.Text.Json source-gen). Facet builder helper. Store `project_id` + `version_id` + sha512 per installed file in the instance manifest so updates can use `/version_files/update`.

## 6. .mrpack format

### L-11 `modrinth.index.json` and archive layout
- **Description:** The pack is a ZIP (MIME `application/x-modrinth-modpack+zip`, extension `.mrpack`) with UTF-8 `modrinth.index.json` at the root. Fields:
  - `formatVersion` (currently **1**), `game` (`"minecraft"` only), `versionId`, `name`, `summary?`.
  - `files[]`, each `{path, hashes{sha1, sha512} (both REQUIRED), env?{client, server: required|optional|unsupported}, downloads[] (HTTPS URLs), fileSize}`.
  - `dependencies` keys: `minecraft`, `forge`, `neoforge`, `fabric-loader`, `quilt-loader`. More may be added, so handle unknown keys.
  Folders: `overrides/` is copied to the instance root. Then `client-overrides/` (client) or `server-overrides/` (server) is applied on top and overwrites. Modrinth only accepts download URLs from `cdn.modrinth.com`, `github.com`, `raw.githubusercontent.com` and `gitlab.com`, and says other platforms SHOULD define their own allowlist. Follow at least 3 redirects. **Security:** `path` must not escape the instance directory: reject `..`, drive letters, and a leading `/` or `\`.
- **Source:** https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack (the old docs.modrinth.com/docs/modpacks/format_definition URL now returns 404)
- **Verified:** 2026-10-25.
- **Technical implications:** The importer must: validate every `path` (full-path containment check after `Path.GetFullPath`), apply the same check to override entries (zip-slip), enforce the download domain allowlist, verify sha512 (preferred) or sha1, prompt for files whose `env.client` is `optional`, skip files whose `env.client` is `unsupported`, then map `dependencies` to our loader installers (L-01, L-02, L-05, L-07).
- **Confidence:** High.
- **Limitations:** The help article is the only spec text, and it has no JSON Schema.
- **Selected approach:** `MrpackImporter`: parse → validate (paths, domains, hashes required) → create the instance (MC + loader from `dependencies`) → download files in parallel with hash checks → extract `overrides` then `client-overrides` with zip-slip guards. Exporter: write `formatVersion: 1`, include only Modrinth-resolvable files in `files[]` (via `/version_files`), put the rest in `overrides/`, and never include game jars, libraries or assets (JAVA_ENGINE.md J-16).

---

## 7. Summary: how to install each loader

| Loader | Method | Java needed at install? | Status |
|---|---|---|---|
| Fabric | GET `meta.fabricmc.net/v2/versions/loader/{mc}/{loader}/profile/json` → save → vanilla engine merges | No | MVP |
| Quilt | Same as Fabric, using `meta.quiltmc.org/v3` | No | MVP |
| NeoForge | Download installer from `maven.neoforged.net`, run `java -jar … --install-client <root>` with a stub `launcher_profiles.json`, read the produced version JSON | Yes (Mojang runtime of the target MC) | MVP |
| Forge ≥1.12.2 | Download installer from `maven.minecraftforge.net`, run `--installClient <root>`, same stub | Yes | MVP |
| Forge 1.7.10–1.11.x | Extract `versionInfo` and the universal jar from the installer (no headless client flag) | No | Later |
| Forge <1.6 | Jar-mod | — | Not supported |
| Native processor runner (Forge/NeoForge) | Parse `install_profile`, run processors ourselves | Yes | Later optimization |
