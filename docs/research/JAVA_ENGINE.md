# Java Edition engine research (vanilla install + launch)

Project: Ninja Launcher (C#/.NET 10 + Avalonia, GPL-3.0-or-later)
Verification date for every finding: **2026-10-25**
Method: live HTTP fetches of the endpoints below (curl), parsing the JSON with scripts, and reading the minecraft.wiki source pages. Where something was not checked live, the finding says so.

Live state seen on 2026-10-25: `latest.release = 26.3`, `latest.snapshot = 26.4-snapshot-5`, 920 versions in the manifest.

Each finding uses the same fields: Description, Source, Verified, Technical implications, Confidence, Limitations, Selected approach.

---

## 1. Version manifest

### J-01 `version_manifest_v2.json`
- **Description:** URL `https://piston-meta.mojang.com/mc/game/version_manifest_v2.json`. Root: `latest{release,snapshot}` and `versions[]`. Each entry has exactly these keys: `id`, `type` (`release`, `snapshot`, `old_beta`, `old_alpha`), `url` (the version JSON), `time`, `releaseTime`, `sha1` (SHA-1 of the version JSON the URL points to), `complianceLevel` (0 or 1; 1 means the version supports the player-safety features, and the official launcher warns when it is 0). v1 (`version_manifest.json`) has no `sha1` or `complianceLevel`.
- **Source:** https://piston-meta.mojang.com/mc/game/version_manifest_v2.json ; https://minecraft.wiki/w/Version_manifest.json
- **Verified:** 2026-10-25. Live fetch; the key set was computed over all 920 entries.
- **Technical implications:** We can check every version JSON we download against `sha1`. Version JSON URLs have the form `https://piston-meta.mojang.com/v1/packages/<sha1>/<id>.json`, so they are content-addressed and immutable, and caching them forever is safe. Version IDs no longer follow `1.x`: since 26.1 they look like `26.3` and `26.4-snapshot-5`, so do not parse them as SemVer or `System.Version`.
- **Confidence:** High.
- **Limitations:** `launchermeta.mojang.com` still serves the same paths. Older JSONs still point at `launchermeta.mojang.com` and `launcher.mojang.com` URLs (seen in the 1.7.10 asset index and logging URLs). Treat both hosts as valid.
- **Selected approach:** Fetch v2 with ETag/If-Modified-Since, cache it on disk, and fall back to the cache when offline. Verify each version JSON's SHA-1 and store it at `versions/<id>/<id>.json`. Order versions by `releaseTime`, never by parsing the ID.

---

## 2. Version JSON (`client.json`)

### J-02 Top-level fields
- **Description:** Keys observed in 26.3: `arguments`, `assetIndex`, `assets`, `complianceLevel`, `downloads`, `id`, `javaVersion`, `libraries`, `logging`, `mainClass`, `minimumLauncherVersion` (21), `releaseTime`, `time`, `type`. In 1.12.2 and older, `minecraftArguments` (one string) replaces `arguments`. 1.5.2 and older have no `logging`. 1.6.4 has no `javaVersion`.
  - `downloads`: `client{sha1,size,url}` and `server`. `client_mappings` and `server_mappings` exist from 1.14.4 to 1.21.x, and are **gone from 26.1 on** because the game is no longer obfuscated. Old versions also have `windows_server`.
  - `assetIndex{id,sha1,size,totalSize,url}` plus `assets` (the same ID). Examples: 26.3 uses `34`, 1.21.1 uses `17`, 1.12.2 uses `1.12`, 1.7.2 and 1.6.4 use `legacy`, 1.5.2 and b1.7.3 use `pre-1.6`.
  - `javaVersion{component,majorVersion}`. Examples: 26.x uses `java-runtime-epsilon`/25, 1.20.5–1.21.x use `java-runtime-delta`/21, 1.18.2 uses `java-runtime-beta`/17, 1.19–1.20.4 use `java-runtime-gamma`/17, ≤1.16.5 use `jre-legacy`/8.
  - `logging.client{argument:"-Dlog4j.configurationFile=${path}", file{id,sha1,size,url}, type:"log4j2-xml"}`. Files seen: `client-1.7.xml`, `client-1.12.xml`, `client-1.21.2.xml`.
  - `mainClass`: `net.minecraft.client.main.Main` for 1.6 and later, `net.minecraft.launchwrapper.Launch` for 1.5.2 and betas, `com.mojang.rubydung.RubyDung` for rd-132211.
- **Source:** the live version JSONs for 26.3, 26.1, 1.21.1, 1.20.5, 1.20.4, 1.19, 1.18.2, 1.16.5, 1.12.2, 1.7.10, 1.7.2, 1.6.4, 1.5.2, b1.7.3 and rd-132211 (URLs come from the manifest) ; https://minecraft.wiki/w/Client.json
- **Verified:** 2026-10-25.
- **Technical implications:** The model must allow `arguments`/`minecraftArguments` to be absent, and `logging`/`javaVersion` to be null. When `javaVersion` is missing, default to `jre-legacy`/8. Mappings must be optional.
- **Confidence:** High.
- **Limitations:** Only the 15 versions listed were sampled, not all 920.
- **Selected approach:** Use System.Text.Json source-generated records with nullable members (AOT-safe), and keep unknown properties in `JsonExtensionData` so loader JSONs can round-trip.

### J-03 `arguments.game` / `arguments.jvm` / `arguments.default-user-jvm` (new) and the legacy `minecraftArguments`
- **Description:** Since 1.13 (17w43a), `arguments.game` and `arguments.jvm` are arrays of either a plain string or `{rules[], value: string|string[]}`. Game rules use `features`: `is_demo_user`, `has_custom_resolution`, `has_quick_plays_support`, `is_quick_play_singleplayer`, `is_quick_play_multiplayer`, `is_quick_play_realms` (quick play arrived in 1.20/23w14a). JVM rules use `os`.
  **New in 26.1-snapshot-2:** `arguments.default-user-jvm`. These are the *defaults for the user's custom JVM arguments field*. In 26.3 they are `-Xms2G -Xmx4G -XX:+UseCompactObjectHeaders -XX:+AlwaysPreTouch -XX:+UseStringDeduplication`, plus `-XX:+UseZGC` on osx, linux and Windows ≥10.0.17134, or G1GC flags on older Windows. These rules use a new `os.versionRange{min|max}` field.
  26.3 `jvm` also adds `--enable-native-access=ALL-UNNAMED`, `--add-exports java.base/jdk.internal.misc=ALL-UNNAMED`, and points native paths at subdirectories: `${natives_directory}/java`, `/jna`, `/lwjgl`, `/netty`.
  26.3 `game` **no longer contains `--userType ${user_type}`** (1.21.1 still does).
  Legacy (≤1.12.2): `minecraftArguments` is one space-separated string, e.g. `--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userType ${user_type} --versionType ${version_type}`. For 1.5.2 it is `${auth_player_name} ${auth_session} --gameDir ${game_directory} --assetsDir ${game_assets}`. Legacy versions have no JVM args in the JSON, so the launcher must supply `-Djava.library.path=${natives_directory}` and `-cp ${classpath}` itself.
- **Source:** the live 26.3, 1.21.1, 1.12.2, 1.7.10 and 1.5.2 JSONs ; https://minecraft.wiki/w/Client.json (raw wikitext, which documents `default-user-jvm` and `versionRange` and gives the history line "26.1 snap2: Added default-user-jvm")
- **Verified:** 2026-10-25.
- **Technical implications:** The argument builder needs two code paths (modern and legacy) and must split legacy strings on spaces. `default-user-jvm` is not something the game requires. It is a suggestion for what to pre-fill in the instance's "JVM arguments" box, and the user's own heap and GC choices must replace it, not be added to it. If we added it next to our own `-Xmx`, the JVM would get conflicting flags. We need a `versionRange` comparison on the OS version (Windows build `10.0.17134` = 1803).
- **Confidence:** High for the shapes. Medium for how `default-user-jvm` is meant to be used: the wiki wording ("default values for a launcher's custom JVM arguments field") is the only description available.
- **Limitations:** We don't know whether the official launcher still adds `-Xmx2G` when the user field is empty. That was not verified.
- **Selected approach:** Build the command line in this order: JVM args from the JSON (rules evaluated), the logging argument, then the user JVM args. The user JVM args are the instance override if set, otherwise our heap setting plus `default-user-jvm` evaluated by rules. Then `mainClass`, then game args (rules plus the feature set we enable). For legacy versions, synthesize `-Djava.library.path=${natives_directory} -cp ${classpath}`.

### J-04 Rule evaluation (`rules[]` with `os`, `features`)
- **Description:** A rule is `{action: "allow"|"disallow", os?{name?, arch?, version?, versionRange?}, features?{...}}`. Algorithm (Mojang semantics, the same one CmlLib uses): with no rules, the item is allowed. Otherwise start at *disallowed* and walk the rules in order. If every condition of a rule matches, set the result to that rule's action. The last matching rule wins.
  `os.name` is `windows`, `osx` or `linux`. `os.arch` is `x86` (meaning a 32-bit OS/JVM). `os.version` is a **regex** matched against the OS version string; the only live example is 1.5.2 with `^10\.5\.\d$` on osx. `os.versionRange{min,max}` is new in 26.x. `features` matches when each listed flag equals the launcher's value; an absent flag counts as false.
  Rule shapes seen across versions: `{"action":"allow"}`, `{"action":"disallow","os":{"name":"osx"}}`, `{"action":"allow","os":{"name":"windows"}}`, `{"action":"allow","os":{"arch":"x86"}}` (JVM `-Xss1M`), plus the regex and versionRange forms.
- **Source:** the live JSONs above ; https://minecraft.wiki/w/Client.json ; CmlLib `src/Rules/RulesEvaluator.cs` (master, read 2026-10-25)
- **Verified:** 2026-10-25.
- **Technical implications:** Arm64 is never named in the rules. 26.3 puts both the `natives-windows` and `natives-windows-arm64` libraries under the same `os.name=windows` rule, and LWJGL picks the right binary for the architecture at runtime. Our OS version for Windows must be the real build number: `Environment.OSVersion` is correct on .NET 5 and later.
- **Confidence:** High.
- **Limitations:** For `versionRange`, we have not confirmed whether `min` and `max` are inclusive. In 26.3 both the ZGC and G1 blocks use `10.0.17134` (one as min, one as max), so at exactly 17134 one of them is inclusive. Unverified.
- **Selected approach:** `RuleEvaluator.IsAllowed(rules, OsInfo{Name, Arch:"x86"|"x64"|"arm64", Version}, IReadOnlySet<string> features)`. Compile regexes with a timeout. Compare `versionRange` with `System.Version`, treating `min` as inclusive and `max` as exclusive, which makes 17134 exactly pick ZGC. Unit-test it with the 26.3, 1.12.2 and 1.5.2 rule fixtures.

### J-05 Libraries: `downloads.artifact`, `classifiers`, `natives`, `extract`, `rules`
- **Description:**
  - Modern library: `{name:"g:a:v[:classifier]", downloads:{artifact:{path,sha1,size,url}}, rules?}`.
  - Legacy native library (≤1.18.2): `{name, downloads:{artifact?, classifiers:{"natives-windows":{...},...}}, natives:{"windows":"natives-windows","osx":"natives-osx","linux":"natives-linux"}, extract:{exclude:["META-INF/"]}, rules?}`. The `natives` value can contain `${arch}` (e.g. 1.7.10 `tv.twitch:twitch-platform` → `natives-windows-${arch}`, where `${arch}` is replaced by `32` or `64`).
  - Since 1.19 (22w18a for Windows ARM, then all platforms from 1.19-pre1), `natives` and `classifiers` are no longer used. Natives are separate library entries named `g:a:v:natives-<platform>` with an `os.name` rule. Platforms seen in 26.3: `natives-linux`, `natives-macos`, `natives-macos-arm64`, `natives-windows`, `natives-windows-arm64`. 1.21.1 also has `natives-windows-x86` and `natives-macos-patch`.
  - Counts: 26.3 has 114 libraries, 55 of them `:natives-*`. 1.19 has 42 classifier natives and 0 legacy ones. 1.18.2 has 16 legacy native libs.
  - Loader libraries (Fabric/Quilt) carry only `{name, url:<maven base>}` with optional hashes. See MOD_LOADERS.md.
- **Source:** the live 26.3, 1.21.1, 1.19, 1.18.2, 1.12.2 and 1.7.10 JSONs ; https://minecraft.wiki/w/Client.json history lines for 1.19
- **Verified:** 2026-10-25.
- **Technical implications:** Modern native jars go **on the classpath**, and LWJGL extracts them itself into `-Dorg.lwjgl.system.SharedLibraryExtractPath`. Legacy native jars are **extracted** into `${natives_directory}` (minus `extract.exclude`) and are **not** on the classpath. A legacy library can have both `artifact` and `classifiers`; the artifact goes on the classpath.
- **Confidence:** High.
- **Limitations:** We did not survey every 1.19–1.21 JSON for `arch` rules on `natives-windows-x86`. In 1.21.1 there are none, and all Windows natives match `name=windows`.
- **Selected approach:** A `LibraryResolver` yields `(ClasspathJars, NativeArchivesToExtract)`. Dedupe classpath entries by `group:artifact:classifier`, keeping the first one, with child (loader) libraries placed before parent libraries. Extract legacy natives into a per-launch directory (`natives/<instance>/<version>`) and wipe it before each launch.

### J-06 Library URL and path from Maven coordinates
- **Description:** `group:artifact:version[:classifier][@ext]` maps to `group.Replace('.','/')/artifact/version/artifact-version[-classifier].<ext ?? "jar">`. The URL is `<repoBase>/<path>`. Mojang's default base is `https://libraries.minecraft.net/`. When `downloads.artifact.path`/`url` exist, use them as given. Fabric/Quilt give `url` as the repo base. Forge/NeoForge processor args use `[g:a:v:c@ext]` (e.g. `[net.neoforged:neoform:1.21.1-20240808.144430@zip]`).
- **Source:** the live 26.3 library (`org/lwjgl/lwjgl/3.4.3/lwjgl-3.4.3-natives-windows.jar` under `libraries.minecraft.net`) ; the Fabric profile JSON ; the NeoForge install_profile.json (see MOD_LOADERS.md). HEAD checks on the libraries.minecraft.net, maven.fabricmc.net, maven.neoforged.net and maven.minecraftforge.net URLs all returned 200.
- **Verified:** 2026-10-25.
- **Technical implications:** One `MavenCoordinate` parser serves vanilla, loaders and processors. When a library has no sha1 (e.g. `net.fabricmc:fabric-loader` in the Fabric profile), fetch `<url>.sha1`, which exists on the Fabric, NeoForge and Forge mavens (HEAD 200).
- **Confidence:** High.
- **Limitations:** Some old Forge libraries used `.pack.xz` or needed `clientreq`/`serverreq` handling (pre-1.13). This was not re-verified live.
- **Selected approach:** `MavenCoordinate.Parse(string)` → `RelativePath`. Verify downloads with SHA-1 when known, then fall back to `.sha1` sidecar files. Write to a temporary file and move it into place atomically.

### J-07 `inheritsFrom` (used by every loader)
- **Description:** Loader profiles are partial version JSONs with `inheritsFrom: "<vanilla id>"`, their own `id`, `mainClass`, `libraries`, and `arguments` (or legacy `minecraftArguments`). Examples: Fabric `fabric-loader-0.20.1-26.3` → `26.3`; NeoForge `neoforge-21.1.265` → `1.21.1`; Forge `1.12.2-forge-14.23.5.2859` → `1.12.2`.
- **Source:** live Fabric/Quilt profile endpoints and the contents of the NeoForge/Forge installer jars (MOD_LOADERS.md)
- **Verified:** 2026-10-25.
- **Technical implications:** Merge rules (the de-facto standard used by the official launcher, Prism and CmlLib):
  - The child's `mainClass` replaces the parent's.
  - Libraries are child first, then parent, deduped by `g:a[:classifier]` with the first one kept. This matters: Forge and NeoForge ship newer copies of vanilla libraries.
  - `arguments.game` and `arguments.jvm` are parent first, then child appended.
  - The child's `minecraftArguments` *replaces* the parent's (old Forge/LiteLoader repeat the full string).
  - `assetIndex`, `assets`, `javaVersion`, `downloads`, `logging` and `type` are inherited when the child doesn't have them.
  - `${version_name}` should be the child ID.
  - The client jar is the parent's `downloads.client`, unless the child sets `jar`.
  - Chains can be more than one level deep (they are recursive).
- **Confidence:** Medium-high. The behaviour is consistent across implementations, but Mojang has no formal spec.
- **Limitations:** We did not compare dedupe behaviour against the official launcher for libraries whose `name` is the same but whose version differs.
- **Selected approach:** `VersionMerger.Merge(child, parentResolver)`, recursive, with a cycle guard. Golden-file tests use the Fabric 26.3, NeoForge 21.1.265 and Forge 1.12.2 profiles.

---

## 3. Assets

### J-08 Asset index and object URLs
- **Description:** The index JSON is `{objects:{"<virtual path>":{hash,size}}}` plus optional flags. Objects live at `https://resources.download.minecraft.net/<hash[0..2]>/<hash>` and are stored locally at `assets/objects/<hash[0..2]>/<hash>`. The index is stored at `assets/indexes/<assetIndex.id>.json`. Index 34 (for 26.3): `totalSize` is 484,894,038 bytes. Index 17 (for 1.21.1): 826 MB.
- **Source:** the live index `https://piston-meta.mojang.com/v1/packages/0c8f43b28f837869c1e54a6aeb22c396a1548946/34.json` ; HEAD on `https://resources.download.minecraft.net/b6/b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356` → 200 image/png ; https://minecraft.wiki/w/Client.json
- **Verified:** 2026-10-25.
- **Technical implications:** The object store is content-addressed and shared across versions and instances. That needs a parallel, bounded downloader with SHA-1 verification. 26.x indexes are smaller than 1.21.x ones.
- **Confidence:** High.
- **Limitations:** No published rate limit. Assume a CDN, and use around 8–16 concurrent connections.
- **Selected approach:** One shared `assets/` under the launcher data root. Skip files that already exist when their size matches, and re-verify the hash in "repair" mode.

### J-09 Legacy and virtual assets (`legacy`, `pre-1.6`)
- **Description:** `legacy.json` (used by 1.6.x–1.7.2) has `"virtual": true`: objects must also be copied to `assets/virtual/legacy/<path>`. `pre-1.6.json` (≤1.5.2, betas, alphas) has `"map_to_resources": true`: objects must be copied to `<gameDir>/resources/<path>`. For these versions `${game_assets}` = the virtual/resources directory, and `${assets_root}` is still `assets/`.
- **Source:** live `https://launchermeta.mojang.com/v1/packages/770572e819335b6c0a053f8378ad88eda189fc14/legacy.json` (keys: `virtual`, `objects`) ; live `pre-1.6.json` (keys: `map_to_resources`, `objects`) ; https://minecraft.wiki/w/Client.json
- **Verified:** 2026-10-25.
- **Technical implications:** We need a post-download "materialize" step. Copy, not hardlink, unless both are on the same volume; hardlinks are fine on NTFS.
- **Confidence:** High for the flags. Medium for whether `${game_assets}` must point at `resources` or `virtual/legacy` for pre-1.6, since implementations differ.
- **Limitations:** Pre-1.6 versions are historically fragile (they need online-mode skin and sound fixes). Very old versions may need extra compatibility work.
- **Selected approach:** Support it, but mark versions before 1.6 as "best effort" in the UI. Pick `virtual/legacy` when `virtual`, and `<gameDir>/resources` when `map_to_resources`.

---

## 4. Natives extraction

### J-10 Legacy extraction vs modern classpath natives
- **Description:** Before 1.19: for each allowed library with a `natives` map entry for the current OS, replace `${arch}` with `32`/`64`, download `downloads.classifiers[<classifier>]`, and unzip it into `${natives_directory}`, skipping `extract.exclude` prefixes (always `META-INF/` in the samples). From 1.19 on: the `natives-<platform>` jars go on the classpath. 1.19–1.21.x pass `-Djava.library.path=${natives_directory}`, `-Djna.tmpdir`, `-Dorg.lwjgl.system.SharedLibraryExtractPath` and `-Dio.netty.native.workdir`, all set to `${natives_directory}`. 26.3 uses subfolders (`/java`, `/jna`, `/lwjgl`, `/netty`).
- **Source:** the live 1.7.10, 1.12.2, 1.18.2, 1.19, 1.21.1 and 26.3 JSONs
- **Verified:** 2026-10-25.
- **Technical implications:** We must always create `${natives_directory}`, even for modern versions, because the JVM args reference it. Old LWJGL2 natives have no Windows arm64 or macOS arm64 builds, so versions ≤1.18 on ARM need an x64 JVM under emulation or community natives. That is out of scope for the MVP.
- **Confidence:** High.
- **Limitations:** Apple Silicon and Windows ARM support for old versions was not tested.
- **Selected approach:** A `NativesExtractor` used only for legacy natives, guarded against zip-slip (refuse entries whose full path falls outside the target). Create the directory per launch and delete it when the game exits.

---

## 5. Placeholder variables

### J-11 Substitution table
- **Description:** `${...}` tokens in `arguments`/`minecraftArguments`/`logging.argument`. Every token below appears in live JSONs or in loader JSONs.

| Token | Meaning / value we supply | Where seen |
|---|---|---|
| `${auth_player_name}` | Profile name | all |
| `${version_name}` | ID of the launched (child) version | all |
| `${game_directory}` | Instance directory | all |
| `${assets_root}` | `<root>/assets` | ≥1.6 |
| `${assets_index_name}` | `assetIndex.id` | ≥1.7.10 |
| `${auth_uuid}` | Profile UUID without dashes | ≥1.7 |
| `${auth_access_token}` | Minecraft access token (offline/demo: any non-empty string, e.g. `0`) | ≥1.7 |
| `${clientid}` | Launcher client ID (official launcher sends a base64 ID). Unverified what the game does with it; we send our own stable per-install ID. | ≥1.19 |
| `${auth_xuid}` | Xbox user ID (XUID) from XSTS `xui[0].xid`; offline: `0` | ≥1.19 |
| `${user_type}` | `msa` for Microsoft accounts (legacy: `mojang`/`legacy`) | 1.7–1.21.x (absent in 26.3 args) |
| `${version_type}` | `type` from the JSON, or a custom label (shown on the title screen) | ≥1.8 |
| `${user_properties}` | `{}` (Twitch-era property map) | 1.7.x–1.8.x |
| `${auth_session}` | Legacy session string; historically `token:<accessToken>:<uuid>` (format unverified live) | ≤1.5.2 |
| `${game_assets}` | Virtual/resources dir (J-09) | ≤1.6 |
| `${natives_directory}` | Per-launch natives dir | jvm args |
| `${launcher_name}` / `${launcher_version}` | `NinjaLauncher` / app version | jvm args ≥1.13 |
| `${classpath}` | Libraries + client jar joined by the separator | jvm args |
| `${classpath_separator}` | `;` on Windows, `:` elsewhere | NeoForge/Forge jvm args |
| `${library_directory}` | `<root>/libraries` | NeoForge/Forge jvm args (`-p` module path, `-DlibraryDirectory`) |
| `${resolution_width}` / `${resolution_height}` | Window size when the `has_custom_resolution` feature is on | ≥1.13 |
| `${quickPlayPath}` | Quick Play log path (relative to the game dir) | ≥1.20 |
| `${quickPlaySingleplayer}` | World folder name | ≥1.20 |
| `${quickPlayMultiplayer}` | `host[:port]` | ≥1.20 |
| `${quickPlayRealms}` | Realm ID | ≥1.20 |
| `${path}` | Local path of the log4j config (only inside `logging.client.argument`) | ≥1.7 |

- **Source:** the live 26.3, 1.21.1, 1.12.2, 1.7.10 and 1.5.2 JSONs ; the NeoForge 21.1.265 `version.json` ; https://minecraft.wiki/w/Client.json ; CmlLib `MinecraftProcessBuilder.cs` (it uses the same token set)
- **Verified:** 2026-10-25.
- **Technical implications:** Unknown tokens must not crash the launch: leave them as they are and log them. Never write the access token to logs. Redact it in any "copy launch command" feature.
- **Confidence:** High for the token names. Medium for `${clientid}` and `${auth_session}` value semantics.
- **Limitations:** Quick Play features must be enabled *only* when the matching value is set. Otherwise the argument value is empty and the game fails to parse it.
- **Selected approach:** A `Dictionary<string,string>` substitution in a single pass with regex `\$\{([^}]+)\}`. Build the feature set from the launch options (`has_custom_resolution`, `has_quick_plays_support` plus exactly one `is_quick_play_*`, `is_demo_user`). Pass the arguments as an argument list (`ProcessStartInfo.ArgumentList`), never as one concatenated string, so spaces in paths are safe.

---

## 6. Mojang Java runtime manifest

### J-12 Runtime index (`all.json`)
- **Description:** URL (still current): `https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`. The same path on `piston-meta.mojang.com` returns a **byte-identical** file (verified). Shape: `{<platform>:{<component>:[{availability{group,progress}, manifest{sha1,size,url}, version{name,released}}]}}`.
  Platform keys: `gamecore` (all empty), `linux`, `linux-i386`, `mac-os`, `mac-os-arm64`, `windows-arm64`, `windows-x64`, `windows-x86`.
  Components and live versions:

| Component | Java | windows-x64 | windows-arm64 | mac-os | mac-os-arm64 | linux | linux-i386 | windows-x86 |
|---|---|---|---|---|---|---|---|---|
| `jre-legacy` | 8 | 8u51 | – | 8u74 | – | 8u202 | 8u202 | 8u51 |
| `java-runtime-alpha` | 16 | 16.0.1.9.1 | – | ✓ | – | ✓ | – | ✓ |
| `java-runtime-beta` | 17 | 17.0.15 | – | ✓ | – | ✓ | – | 17.0.1 |
| `java-runtime-gamma` | 17 | 17.0.15 | 17.0.15 | ✓ | ✓ | ✓ | – | 17.0.8.1 |
| `java-runtime-gamma-snapshot` | 17 | 17.0.15 | 17.0.15 | ✓ | ✓ | ✓ | – | 17.0.8.1 |
| `java-runtime-delta` | 21 | 21.0.7 | 21.0.7 | ✓ | ✓ | ✓ | – | – |
| `java-runtime-epsilon` | 25 | 25.0.1 | **25.0.0** | ✓ | ✓ | ✓ | – | – |
| `minecraft-java-exe` | (launcher shim) | 14 | – | – | – | – | – | 12 |

- **Source:** the URL above (live) and the piston-meta mirror (live, `cmp` identical)
- **Verified:** 2026-10-25.
- **Technical implications:** We map `javaVersion.component` straight to a manifest entry. ARM platforms have **no** `jre-legacy`/`alpha`/`beta`, so versions ≤1.17.1 on ARM need a fallback: an x64 runtime under emulation, or a user-supplied JDK. Even 32-bit Windows lacks `delta`/`epsilon`. Ignore `minecraft-java-exe`. The URL contains a fixed hash, so it could change. CmlLib hardcodes the same URL.
- **Confidence:** High.
- **Limitations:** Mojang's runtime builds are older than current upstream patches (21.0.7 and 25.0.1). Some users may prefer an Adoptium/Temurin JDK, which is out of scope here.
- **Selected approach:** Make the URL configurable (default: the launchermeta URL, fallback: piston-meta). OS/arch detection: `RuntimeInformation.OSArchitecture` → platform key. If the component is missing for our platform, show a clear message and let the user pick a Java executable manually.

### J-13 Per-runtime file manifest (`manifest.json`)
- **Description:** `{files:{"<relative path>":entry}}`. Entry types are `directory`, `file` and `link`. `file`: `{type:"file", executable:bool, downloads:{raw{sha1,size,url}, lzma?{sha1,size,url}}}`. `link`: `{type:"link", target:"../relative/path"}`. Objects are served from `https://piston-data.mojang.com/v1/objects/<sha1>/<name>`. Windows delta (21.0.7): 484 entries (82 directories, 402 files, 0 links). macOS beta: 436 entries, **208 links**, and the root is `jre.bundle/Contents/Home/...`. Small files have only `raw`; most have both `raw` and `lzma`.
- **Source:** live `https://piston-meta.mojang.com/v1/packages/cb4394a27089d19f65d5baa6cf0482c27c3c7865/manifest.json` (windows-x64 delta) and `.../b330c3b6040b6693ab2d39b3122fa397ebd2cf69/manifest.json` (mac-os beta/gamma)
- **Verified:** 2026-10-25.
- **Technical implications:** `raw` is always present, so **we can skip LZMA entirely** and avoid an LZMA dependency (.NET has no built-in LZMA decoder). It costs more bandwidth: for example, `api-ms-win-core-console-l1-1-0.dll` is 22 KB raw vs 7 KB lzma. On macOS/Linux we must create symlinks (`File.CreateSymbolicLink`) and set the executable bit (`File.SetUnixFileMode`). Verify the SHA-1 of the *raw* bytes. The Java binary path is `bin/javaw.exe` on Windows (`bin/java` for a console), and `jre.bundle/Contents/Home/bin/java` on macOS.
- **Confidence:** High.
- **Limitations:** We did not measure total download size with raw vs lzma. Estimate about 2–3× bandwidth for the runtime only, which is a one-time cost.
- **Selected approach:** Download `raw` only in v1 and keep a seam (`IRuntimeFileFetcher`) for adding LZMA later. Install into `runtimes/<component>/<platform>/`. Write a `.version` stamp file containing the manifest sha1, and re-verify when it changes. Validate that link targets resolve inside the runtime directory (zip-slip-style guard).

---

## 7. CmlLib.Core evaluation

### J-14 CmlLib.Core facts
- **Description:**
  - **Licence:** MIT (GitHub API `license.spdx_id = MIT`; NuGet `licenseExpression = MIT`).
  - **Latest version:** **4.1.0**, published on NuGet 2026-10-17T16:11:49Z (git commit "release: 4.1.0", same date). Previous versions: 4.0.6 (2025-09-24), 4.0.5 (2025-04-18).
  - **Target frameworks:** `net8.0` and `netstandard2.0`, so it runs on .NET 10.
  - **Dependencies:** CmlLib.Core.Commons, LZMA-SDK 19.0.0, SharpZipLib 1.4.2, System.Text.Json ≥8.0.5, System.Threading.Tasks.Dataflow.
  - **Activity:** 283 stars, 45 forks, 7 open issues, not archived, last push 2026-10-25. 4.1.0 added Native AOT compatibility plus JSON regression tests (PR #132). Releases are sparse: about 1 per year since 4.0.0 (2024-07). GitHub Releases only lists up to v4.0.4; later versions exist only on NuGet. It is mostly a single-maintainer project.
  - **Features (from the repo tree and README):** version manifest loading, parallel game installer (`ParallelGameInstaller`), Java runtime from the Mojang manifest (`MinecraftJavaManifestResolver`, LZMA supported), rules evaluator, native extractor, process/argument builder (all modern tokens including quickPlay*), FabricMC/QuiltMC/LiteLoader version loaders in core. Separate NuGet packages: `CmlLib.Core.Installer.Forge` 2.0.0, `CmlLib.Core.Installer.NeoForge` 4.0.1, `CmlLib.Core.Auth.Microsoft` 3.4.0.
  - **Gaps found in master source (2026-10-25):** `LauncherOSRule` only models `name`/`arch`/`version`. **No `versionRange`** and **no `default-user-jvm`** handling anywhere in the source tree (grep found nothing). The default `user_type` is `"Mojang"`. Hosts are hardcoded to `launchermeta.mojang.com`, and the LiteLoader manifest URL uses plain `http://`.
- **Source:** https://github.com/CmlLib/CmlLib.Core (GitHub REST API: repo, releases, commits, tree; source zip of master) ; https://www.nuget.org/packages/CmlLib.Core (NuGet v3 registration and flat-container APIs)
- **Verified:** 2026-10-25.
- **Technical implications:** MIT is compatible with GPL-3.0-or-later (we can ship it, or borrow code with attribution). 26.x launches probably still work, because `default-user-jvm` is optional and simply ignored. But CmlLib lags format changes, and it would decide heap/GC defaults and network hosts for us. It brings LZMA-SDK and SharpZipLib, which we don't need: .NET has `System.IO.Compression`, and LZMA can be skipped (J-13).
- **Confidence:** High for the facts. Medium for "probably launches 26.3", which was not executed.
- **Limitations:** We did not run CmlLib against 26.3 or check its Forge/NeoForge installer packages in depth.
- **Selected approach:** See J-15.

### J-15 Recommendation: native engine behind our own interfaces, with CmlLib.Core as reference and test oracle only
- **Description:** The vanilla engine is roughly 1.5–2.5k lines of C#: manifest, version JSON plus merge, rules, Maven paths, assets, natives, runtime installer and argument builder. The spec has been stable for years and is fully covered by J-01…J-13 above. It is the core of the product, and Mojang just changed it in 26.x (default-user-jvm, versionRange, natives subdirectories, mappings removed, `--userType` dropped), and CmlLib has not yet modelled those changes. On the loader side, the work happens elsewhere anyway: Fabric/Quilt need a single HTTP GET, and Forge/NeoForge need the official installer or a processor runner (MOD_LOADERS.md). CmlLib does not remove that work.
- **Source:** J-03, J-12, J-13 and J-14 evidence.
- **Verified:** 2026-10-25.
- **Technical implications:** We avoid 3 transitive dependencies. We keep full control of hosts, caching, cancellation, progress, logging/redaction and AOT. The cost is writing and testing about 2k lines, with risk of edge-case bugs on old versions (pre-1.6, LWJGL2 natives).
- **Confidence:** Medium. This is a judgement call. Using CmlLib behind interfaces would also be viable and is the fallback.
- **Limitations:** If schedule pressure dominates, an adapter over CmlLib.Core 4.1.0 is acceptable because the licence is compatible. In that case, put `default-user-jvm` handling in our own layer.
- **Selected approach:** Define the interfaces `IVersionManifestService`, `IVersionResolver` (with merge), `IGameInstaller`, `IJavaRuntimeService` and `ILaunchCommandBuilder` in Core, with our own implementations. Add CmlLib.Core only to the **test** project as a differential oracle: for a fixed set of version JSONs, compare our classpath and argument list with CmlLib's `MinecraftProcessBuilder` output. Do not ship it.

---

## 8. Minecraft EULA and usage guidelines (launcher-relevant)

### J-16 Do not redistribute game files
- **Description:** The EULA says "you must not distribute anything we've made unless we specifically agree to it", which includes "give copies of our game software or content to anyone else". It also says you may develop tools as long as they "do not seem official or approved by us". The usage guidelines say you may distribute a mod "only and not a modded version of Minecraft".
- **Source:** https://www.minecraft.net/en-us/eula ; https://www.minecraft.net/en-us/usage-guidelines
- **Verified:** 2026-10-25 (page text fetched and read).
- **Technical implications:** The launcher must download the client jar, libraries and assets from Mojang's servers on the user's machine. We must not bundle them, mirror them, or ship pre-patched/"modded" client jars, and packs/exports must not embed `versions/*.jar`. Running the Forge/NeoForge processors locally, which patch the jar on the user's machine, is consistent with this.
- **Confidence:** High.
- **Limitations:** Not legal advice.
- **Selected approach:** Download only from official URLs. Modpack export excludes `versions/`, `libraries/`, `assets/` and runtimes.

### J-17 Naming and branding
- **Description:** "You may not use the Minecraft name as the primary or dominant name or title." It may appear in a secondary title or description when that is "necessary to describe your creations … honestly", the secondary title is not dominant, and no Minecraft logo, font or brand assets are used. Nothing may suggest the product is official or endorsed.
- **Source:** https://www.minecraft.net/en-us/usage-guidelines (sections "Our name", "Essential guidelines")
- **Verified:** 2026-10-25.
- **Technical implications:** "Ninja Launcher" is fine as the product name. "Ninja Launcher – a launcher for Minecraft: Java Edition" is fine as a description. "Minecraft Ninja Launcher" or the current folder name "Ninja Minecraft Launcher" used as a *product title* is risky, because "Minecraft" sits inside the distinctive part of the name. Do not use Mojang's logo, grass-block icon or Minecraft font. Add a footer such as "Not an official Minecraft product. Not approved by or associated with Mojang or Microsoft."
- **Confidence:** High for the rule. Medium for applying it to a mid-title placement.
- **Limitations:** The guidelines can change at any time (the EULA says they "often change").
- **Selected approach:** Product, assembly and window title use "Ninja Launcher". The repo or folder can be renamed later. Ship the disclaimer in the About screen and README.
