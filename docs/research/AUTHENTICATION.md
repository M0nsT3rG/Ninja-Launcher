# Authentication research: Microsoft account sign-in for Ninja Launcher

- Project: Ninja Launcher (C# / .NET 10 / Avalonia, GPL-3.0-or-later)
- Scope: Microsoft identity platform OAuth, Xbox Live, XSTS, Minecraft Services, app ID approval, MSAL.NET versus hand-rolled code, credential storage, prohibited practices
- Verification date for every finding: **2026-10-25**
- Confidence: **High** means a primary or official source states it directly. **Medium** means a community-maintained reference (minecraft.wiki) or a reasoned inference from official text. **Low** means it could not be verified and is flagged as such.

> **Headline blocker:** a newly created Azure app ID cannot call `api.minecraftservices.com` until Mojang reviews it and adds it to an allow list. See F4. Until that approval exists, `login_with_xbox` fails with HTTP 403 ("Invalid app registration, see https://aka.ms/AppRegInfo"). The project owner has to file the request. Nothing in code can work around it.

---

## F1. Microsoft identity platform: authorization code flow with PKCE for a desktop public client

### F1.1 Endpoints and the consumers tenant
- **Description:** The authorize endpoint is `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize` and the token endpoint is `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token`. Valid tenant values are `common`, `organizations`, `consumers` and tenant IDs. `consumers` is the authority for "personal Microsoft accounts (MSA) only". minecraft.wiki says you *must* use `consumers` for the `XboxLive.signin` scope, and that `common` or a tenant ID "will just give errors".
- **Source URL:** https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow (ms.date 2026-01-09) ; https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-application-configuration ; https://minecraft.wiki/w/Microsoft_authentication
- **Verification date:** 2026-10-25
- **Technical implications:** Hard-code the authority `https://login.microsoftonline.com/consumers`. Work or school (Entra) accounts cannot sign in, and that is expected.
- **Confidence:** High for the endpoints. Medium for "must be consumers" (wiki statement, consistent with MS docs).
- **Limitations:** Microsoft does not publish a statement tying `XboxLive.signin` to `consumers`.
- **Selected implementation approach:** Authority = `https://login.microsoftonline.com/consumers`. Register the app with a supported account type that includes personal accounts (F1.6).

### F1.2 Scopes
- **Description:** The authorization request must include `XboxLive.signin`, "otherwise the next endpoint will complain" (wiki). Microsoft docs say `refresh_token` is "Only provided if `offline_access` scope was requested".
- **Source URL:** https://minecraft.wiki/w/Microsoft_authentication ; https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow
- **Verification date:** 2026-10-25
- **Technical implications:** Request `XboxLive.signin offline_access`. Do not request Graph scopes, which are not needed.
- **Confidence:** High
- **Limitations:** None known.
- **Selected implementation approach:** `scope=XboxLive.signin offline_access` (URL-encoded as `XboxLive.signin%20offline_access`). MSAL adds `openid profile offline_access` automatically, so with MSAL pass only `XboxLive.signin`.

### F1.3 PKCE
- **Description:** `code_challenge` and `code_challenge_method` are "recommended for all application types, both public and confidential clients". Use `S256`. The token request must send the same `code_verifier`. "Public clients ... must not use secrets or certificates when redeeming an authorization code." Authorization codes "typically ... expire after about 1 minute". `state` should be random and checked on return (CSRF).
- **Source URL:** https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow ; RFC 7636 https://datatracker.ietf.org/doc/html/rfc7636
- **Verification date:** 2026-10-25
- **Technical implications:** The verifier is 43 to 128 characters from `[A-Za-z0-9-._~]` (32 random bytes encoded as base64url gives 43 characters). The challenge is `BASE64URL-NOPAD(SHA256(ASCII(verifier)))`. Never send a `client_secret`.
- **Confidence:** High
- **Limitations:** None.
- **Selected implementation approach:** `RandomNumberGenerator.GetBytes(32)` gives the verifier. `SHA256.HashData` gives the challenge. `state` comes from 16 random bytes and is compared in constant time. With MSAL, the library does all of this.

### F1.4 Loopback redirect URI rules
- **Description:** HTTP is allowed only for localhost. "the port component ... is ignored for the purposes of matching a localhost redirect URI", so `http://localhost` matches `http://localhost:5000/...`. Do not register several localhost URIs that differ only by port. "The IPv6 loopback address (`[::1]`) isn't currently supported." Microsoft recommends `127.0.0.1` over `localhost`, but the portal text box rejects `http://127.0.0.1`, which can only be added through the manifest's `replyUrlsWithType`. Redirect URIs without a path come back with a trailing `/`. Query parameters are not allowed in redirect URIs for apps that sign in personal accounts. The default `response_mode` for a code is `query`.
- **Source URL:** https://learn.microsoft.com/en-us/entra/identity-platform/reply-url (ms.date 2025-05-14)
- **Verification date:** 2026-10-25
- **Technical implications:** Register `http://localhost` (no port, no path) under the **Mobile and desktop applications** platform. At runtime, pick a free ephemeral port and send `redirect_uri=http://localhost:{port}`. The token request must send the identical string.
- **Confidence:** High
- **Limitations:** If `localhost` resolves to `::1` first and the listener is IPv4-only, the redirect fails. Bind the listener for `localhost` (both loopbacks) or use MSAL, which handles this. The portal's restriction on `127.0.0.1` makes `localhost` the practical choice.
- **Selected implementation approach:** Registered URI is `http://localhost`. Runtime redirect is `http://localhost:{freePort}`. The listener accepts exactly one `GET /?code=...&state=...` (or `error=...`), returns a small "you can close this tab" HTML page, then shuts down. Time out after about 5 minutes (MSAL docs recommend "a timeout of at least a few minutes").

### F1.5 Refresh token behaviour and lifetime
- **Description:** The default refresh token lifetime is "**90 days** for all other scenarios" (24 hours applies only to SPAs and email OTP). "Refresh tokens replace themselves with a fresh token upon every use. The Microsoft identity platform doesn't revoke old refresh tokens ... Securely delete the old refresh token after acquiring a new one." Tokens "can be revoked by the sign-in service at any time", for example after a password change, when the user revokes, or on admin action. The refresh request is `grant_type=refresh_token` with `client_id`, `refresh_token` and optionally `scope` (equal to or a subset of the original). The access token `expires_in` is about 3599 seconds in examples.
- **Source URL:** https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens (ms.date 2025-11-05) ; https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow
- **Verification date:** 2026-10-25
- **Technical implications:** Persist only the newest refresh token and overwrite it on every refresh. On `invalid_grant` or `interaction_required`, delete the stored token and fall back to interactive sign-in.
- **Confidence:** High
- **Limitations:** The docs do not say whether the 90-day window slides on use for personal accounts. Treat it as "probably long-lived, but may fail at any time".
- **Selected implementation approach:** Each launch refreshes and stores the new refresh token atomically. Any refresh failure other than a transient network error or a 5xx response wipes the token and asks the user to sign in again.

### F1.6 Azure app registration (free) and "Allow public client flows"
- **Description:** Prerequisites stated by Microsoft: "An Azure account that has an active subscription. Create an account for free", plus a tenant (the **Default Directory** is fine). Steps: Microsoft Entra admin center (https://entra.microsoft.com) > **Entra ID** > **App registrations** > **New registration** > Name > **Supported account types** ("Personal accounts only" or "Any Entra ID Tenant + Personal Microsoft accounts") > **Register** > copy the **Application (client) ID** and **Directory (tenant) ID**. Then **Authentication** > **Add a platform** > **Mobile and desktop applications** > custom redirect URI `http://localhost`. The "Allow public client flows" toggle sits under **Authentication** > **Advanced settings**, labelled "Enable the following mobile and desktop flows", and defaults to No. Microsoft says it "should be disabled unless" you use Native Authentication, **device code flow**, ROPC or Windows Integrated Auth. The authorization code flow is not in that list.
- **Source URL:** https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app (ms.date 2026-05-14) ; https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-applications (ms.date 2025-05-14) ; https://learn.microsoft.com/en-us/entra/external-id/customers/how-to-run-native-authentication-sample-macos-app (toggle label)
- **Verification date:** 2026-10-25
- **Technical implications:** The auth-code-plus-PKCE flow against a "Mobile and desktop" redirect works without the toggle. The toggle is needed only if a **device code** fallback ships.
- **Confidence:** High for the steps and toggle semantics. **Low** for "free": app registrations carry no charge, but whether an Azure free-account signup needs card or phone verification in the owner's country was not verified.
- **Limitations:** Personal Microsoft accounts no longer get a directory automatically, so a tenant is required (via an Azure account). Portal labels change often, so expect small wording differences. The tenant ID is required later by the Mojang review form (F4).
- **Selected implementation approach:** One registration named **"Ninja Launcher"**. The name must NOT contain Mojang, Minecraft, Microsoft, Live, Xbox, Discord or Hypixel, per the review form (F4). Choose "Personal accounts only" (or "Any Entra ID Tenant + Personal Microsoft accounts"). Platform is Mobile and desktop with redirect `http://localhost`. No client secret, no API permissions beyond the default. Leave "Allow public client flows" = **No** unless device code is implemented, then set it to **Yes**.

---

## F2. Xbox Live user authentication and XSTS

### F2.1 Xbox Live user authenticate
- **Description:** `POST https://user.auth.xboxlive.com/user/authenticate` with `Content-Type: application/json` and `Accept: application/json`, both required. Body: `{"Properties":{"AuthMethod":"RPS","SiteName":"user.auth.xboxlive.com","RpsTicket":"d=<MS access token>"},"RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT"}`. The response contains `IssueInstant`, `NotAfter`, `Token` (the XBL token) and `DisplayClaims.xui[0].uhs` (the user hash). The example lifetime is about 14 days.
- **Source URL:** https://minecraft.wiki/w/Microsoft_authentication
- **Verification date:** 2026-10-25
- **Technical implications:** Prefix the RPS ticket with `d=` (MSAL and other v2 tokens). The wiki also says the endpoint "will complain if your SSL implementation does not support SSL renegotiations".
- **Confidence:** Medium. Microsoft or Xbox do not publicly document this; it is community reverse-engineered but widely used (Prism, CmlLib and others).
- **Limitations:** Not an official contract and could change. The TLS renegotiation remark is unverified for .NET on Linux/macOS, so test on all three OSes.
- **Selected implementation approach:** Use exactly the body above. Do not cache the XBL token; re-derive it on each sign-in or refresh.

### F2.2 XSTS authorize
- **Description:** `POST https://xsts.auth.xboxlive.com/xsts/authorize` with the same JSON headers. Body: `{"Properties":{"SandboxId":"RETAIL","UserTokens":["<XBL token>"]},"RelyingParty":"rp://api.minecraftservices.com/","TokenType":"JWT"}`. The response has the same shape. `Token` is the XSTS token and `uhs` matches step F2.1. The example lifetime is about 16 hours.
- **Source URL:** https://minecraft.wiki/w/Microsoft_authentication
- **Verification date:** 2026-10-25
- **Technical implications:** The trailing slash in `rp://api.minecraftservices.com/` matters. Take `uhs` from the XSTS response.
- **Confidence:** Medium
- **Limitations:** As F2.1.
- **Selected implementation approach:** As above.

### F2.3 XSTS error codes (HTTP 401)
- **Description:** On failure XSTS returns 401 with `{"Identity":"0","XErr":<number>,"Message":"","Redirect":"..."}`. Known codes:
  - `2148916227`: account banned from Xbox.
  - `2148916233`: no Xbox account. The user must create one (signing in at minecraft.net does this).
  - `2148916235`: Xbox Live not available or banned in the account's country.
  - `2148916236` / `2148916237`: adult verification required (South Korea).
  - `2148916238`: child account that must be added to a Microsoft Family by an adult. "This only seems to occur when using a custom Microsoft Azure application", which applies to us.
  - `2148916262`: unknown/rare.

  The `Redirect` URL "usually will not resolve" in a browser.
- **Source URL:** https://minecraft.wiki/w/Microsoft_authentication
- **Verification date:** 2026-10-25
- **Technical implications:** Map each XErr to a specific, localizable user message. Do not open `Redirect`.
- **Confidence:** Medium
- **Limitations:** The list is not exhaustive and has no official source. `XErr` is a JSON number larger than Int32, so parse it as `long`/`ulong`.
- **Selected implementation approach:** Use an `enum`-like mapping with a generic fallback message ("Xbox sign-in failed (XErr N)").

---

## F3. Minecraft Services

### F3.1 login_with_xbox
- **Description:** `POST https://api.minecraftservices.com/authentication/login_with_xbox` with JSON headers and body `{"identityToken":"XBL3.0 x=<uhs>;<xsts_token>"}`. The response is `{"username":"<not the player UUID>","roles":[],"access_token":"<Minecraft JWT>","token_type":"Bearer","expires_in":86400}`. The Minecraft access token lasts 24 hours.
- **Source URL:** https://minecraft.wiki/w/Microsoft_authentication ; https://minecraft.wiki/w/Mojang_API
- **Verification date:** 2026-10-25
- **Technical implications:** This is the call that fails with 403 for app IDs that are not allow-listed (F4). Known error shapes:
  - Suspension: 403 `{"path":"/authentication/login_with_xbox","details":{"reason":"ACCOUNT_SUSPENDED"},"errorMessage":"Your account has been suspended. Please contact customer service."}` (Mojang API wiki).
  - App not approved: 403 with message text `Invalid app registration, see https://aka.ms/AppRegInfo` (as quoted by a developer on Microsoft Q&A, 2026-02-09). The exact JSON field layout was not verified.
  - The generic Mojang error shape is `{error, errorMessage, cause}` or `{path, errorMessage}`.

  The wiki notes accounts can be auto-suspended for "a high volume of erroneous requests", so do not retry in a loop.
- **Confidence:** Medium for the request and response. Medium for the 403 text (secondary quote).
- **Limitations:** No official documentation. The 429 behaviour for this endpoint is undocumented.
- **Selected implementation approach:** Never retry on a 4xx response. On 403, check whether the body contains "Invalid app registration" or "AppRegInfo" and show an owner-facing "this build's app ID is not approved" error. Otherwise show the `errorMessage`. Use at most one retry with backoff on 5xx or network errors.

### F3.2 Ownership: entitlements
- **Description:** `GET https://api.minecraftservices.com/entitlements/mcstore` with `Authorization: Bearer <MC token>` returns `{"items":[{"name":"product_minecraft","signature":"<jwt>"},{"name":"game_minecraft",...},...],"signature":"<jwt>","keyId":"1"}`. Game Pass users see `product_game_pass_pc` / `product_game_pass_ultimate`. An empty `items` array means not owned. The alternative endpoint `GET /entitlements/license?requestId=<v4 UUID>` returns an empty payload for Game Pass. The signatures are RS256 JWTs that should be verified with Mojang's public key, which is published on the wiki.
- **Source URL:** https://minecraft.wiki/w/Microsoft_authentication ; https://minecraft.wiki/w/Mojang_API
- **Verification date:** 2026-10-25
- **Technical implications:** Prefer `/entitlements/mcstore` because it reports Game Pass.
- **Confidence:** Medium
- **Limitations:** Entitlement names are not officially documented. The public key could rotate.
- **Selected implementation approach:** Treat the account as owned if `items` contains any of `game_minecraft`, `product_minecraft`, `product_game_pass_pc` or `product_game_pass_ultimate`. Signature verification is optional for the MVP (Mojang's servers enforce ownership at join time anyway). If added later, pin the wiki's PEM.

### F3.3 Profile
- **Description:** `GET https://api.minecraftservices.com/minecraft/profile` with a Bearer token. 200 returns `{"id":"<uuid, no dashes>","name":"<username>","skins":[...],"capes":[...]}`. If there is no profile it returns 404 `{"path":"/minecraft/profile","error":"NOT_FOUND","errorMessage":"..."}`. Game Pass users who never opened the official launcher have no profile yet.
- **Source URL:** https://minecraft.wiki/w/Microsoft_authentication
- **Verification date:** 2026-10-25
- **Technical implications:** The game launch needs `id`, `name` and the MC `access_token`.
- **Confidence:** Medium
- **Limitations:** As above.
- **Selected implementation approach:** A 404 with owned entitlements shows "Create your Minecraft profile name at minecraft.net first". A 404 without entitlements shows "This account does not own Minecraft: Java Edition".

### F3.4 Token lifetimes summary
| Token | Lifetime | Source / confidence |
|---|---|---|
| Authorization code | ~1 minute | MS docs, High |
| MS access token | ~1 hour (`expires_in` 3599 in examples) | MS docs, High |
| MS refresh token | 90 days default, rotates on use, revocable | MS docs, High |
| XBL user token | ~14 days (example) | wiki, Medium |
| XSTS token | ~16 hours (example) | wiki, Medium |
| Minecraft access token | 86400 s (24 h) | wiki, Medium |

---

## F4. CRITICAL: Mojang app ID approval (allow list)

- **Description:** The official Minecraft Help article **"Java Edition Game Service API Review or Application Process"** says: "We're enforcing new policies on third-party apps that seek to access Java game service APIs. From now on, we will be reviewing and manually adding all future API integration requests to an allow list. Existing applications, such as launchers and websites, will continue to have access without interruption. However, new applications must request access via this form." The form link is **https://aka.ms/mce-reviewappid**, which redirects to a Microsoft Forms page titled **"AppID Review"**. The questions contact address is `enforce@minecraft.net`. The 403 error message points to `https://aka.ms/AppRegInfo`, which redirects to this same help article. The page is JS-rendered; it was read in a browser on 2026-10-25.
- **What the AppID Review form states (2026-10-25):**
  - Applications "must verify the user's game ownership and require authentication before enabling gameplay or access to servers or sites. Applications that bypass or disable authentication, ownership or license checks, or other safety features will not be approved. This requirement applies regardless of server configuration."
  - "new application names must not include Mojang, Minecraft, Microsoft, Live, Xbox, Discord, or Hypixel."
  - "Submissions are reviewed weekly. Sending multiple submissions will not speed up the process."
  - Fields:
    1. Confirmation that you have read the EULA and Usage Guidelines (http://aka.ms/mcusageguidelines).
    2. Contact email. "we will need to cross reference your contact information in the Azure Portal", so use the same email as the Azure account.
    3. Request type: "New AppID for Approval".
    4. Application name.
    5. Application (client) ID GUID, one per submission.
    6. Directory (tenant) ID GUID, "Required for new app approval".
    7. Associated website or domain.
    8. Justification: "Submissions that do not have a valid justification will not be reviewed".
    9. Other info.
- **Source URL:** https://help.minecraft.net/hc/en-us/articles/16254801392141 ; https://aka.ms/mce-reviewappid (forms.cloud.microsoft "AppID Review") ; https://aka.ms/AppRegInfo ; corroboration: https://minecraft.wiki/w/Microsoft_authentication ("If your App don't have the Permission `api.minecraftservices.com` will return a 403") ; https://learn.microsoft.com/en-gb/answers/questions/5768276/how-to-get-xboxlive-signin-permission-for-azure-ap (hobbyist hitting the 403, 2026-02-09)
- **Verification date:** 2026-10-25
- **Technical implications:** Steps F1 to F2 (Microsoft and Xbox) work with any app ID. F3 (`login_with_xbox`) returns 403 until the app ID is approved, so nothing past Xbox sign-in can be tested end to end before approval. Approval is discretionary and the form promises no turnaround beyond "reviewed weekly". The launcher's offline or demo mode must not bypass ownership checks for online play, or approval is denied.
- **Confidence:** High for the requirement, form URL and form wording (read first-hand from the official pages). Low on the outcome: the Microsoft Q&A accepted answer (a community volunteer) claims Xbox Developer Program / ID@Xbox enrolment is needed. That claim is **not** stated on the official Mojang page or form and is treated as unverified.
- **Limitations:** No official SLA and no published acceptance criteria beyond the form text. It is unknown whether a GPL fork's own build (a different app ID) would need its own approval. It almost certainly would, since approval is per app ID.
- **Selected implementation approach:** The client ID is a build-time configuration value (`NinjaLauncher.Auth.ClientId`), not hard-coded in many places. Forks must register and get approval for their own ID; README and CONTRIBUTING must say so. A distinct, clear error explains when `login_with_xbox` returns 403 "Invalid app registration".

---

## F5. MSAL.NET versus a hand-rolled HTTP flow

- **Description:** MSAL.NET (`Microsoft.Identity.Client`) is **MIT licensed**, which is compatible with GPL-3.0-or-later. Its README says 4.x is "Active", with supported versions from 4.77.1. On .NET 6+ (including .NET 10, non-Windows-specific targets) the only browser option is the **system browser**, which "requires `http://localhost` redirect URI". With `WithRedirectUri("http://localhost")`, "MSAL.NET will find a random open port and use it". It uses `xdg-open` on Linux (running under `sudo` throws) and `open <url>` on macOS. `SystemWebViewOptions` customizes the success and error page. MSAL recommends a `CancellationToken` timeout because it "cannot detect if the user navigates away or simply closes the browser". The companion package `Microsoft.Identity.Client.Extensions.Msal` provides a cross-platform persisted token cache: DPAPI on Windows, Keychain on macOS (`WithMacKeyChain`) and libsecret keyring on Linux (`WithLinuxKeyring`). It also offers an opt-in plaintext fallback (`WithUnprotectedFile`). Microsoft's protocol page says hand-crafting raw HTTP requests is something "we do **not** recommend".
- **Source URL:** https://github.com/AzureAD/microsoft-authentication-library-for-dotnet ; https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers ; https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization ; https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow
- **Verification date:** 2026-10-25
- **Technical implications:**
  - **MSAL pros:** PKCE, state, loopback listener (dual-stack), browser launch on all three OSes, refresh-token rotation, retry and throttling handling, and an `AcquireTokenSilent` cache are all done and maintained by Microsoft. It is MIT licensed. A C# Minecraft auth library (CmlLib.Core.Auth.Microsoft, listed on the wiki) already uses MSAL with `XboxLive.signin`, which shows the scope works through MSAL.
  - **MSAL cons:** extra dependency (`Microsoft.Identity.Client` plus `Microsoft.IdentityModel.Abstractions`). The token cache is an opaque blob. Less control over the loopback HTML page beyond `SystemWebViewOptions`. On Linux the Extensions cache P/Invokes `libsecret-1.so` and must be verified at runtime.
  - **Hand-rolled pros:** zero dependencies, full control, and only about 3 HTTP calls plus a tiny listener.
  - **Hand-rolled cons:** security-sensitive code to own (state, PKCE, listener binding and IPv6, browser launch quirks on three OSes, rotation) and contrary to Microsoft's recommendation.
- **Confidence:** High for the licence and the documented features. Medium that the `XboxLive.signin` access token from MSAL is accepted as `RpsTicket` with `d=` (based on community libraries; verify in a spike).
- **Limitations:** The latest MSAL version number was not verified here; pin the newest 4.x stable from NuGet when implementing. `MsalCacheHelper.VerifyPersistence()` exists in the Extensions library from prior knowledge but was not re-read in today's docs (Medium).
- **Selected implementation approach:** **Use MSAL.NET** (`PublicClientApplicationBuilder.Create(clientId).WithAuthority("https://login.microsoftonline.com/consumers").WithRedirectUri("http://localhost").Build()`). Call `AcquireTokenSilent` first, then `AcquireTokenInteractive(["XboxLive.signin"])` with a 5-minute `CancellationToken`. Write the Xbox, XSTS and Minecraft calls by hand with `HttpClient`, since there is no library for them. For the MS refresh token, either plug MSAL's cache into the platform store from F6 via `Microsoft.Identity.Client.Extensions.Msal` (least code; it uses native APIs, not the CLI), or serialize the MSAL cache blob into the F6 `ICredentialStore`. **Never** enable `WithUnprotectedFile()`.

---

## F6. Secure credential storage

What to store: one secret per account, which is the Microsoft refresh token (or MSAL's serialized cache blob). The Minecraft access token (24 h) stays **in memory only** and is re-derived at launch. Non-secret profile data (UUID, name, skin URL) may go in plain config.

### F6.1 Windows: DPAPI
- **Description:** `System.Security.Cryptography.ProtectedData` wraps Windows DPAPI. "It provides protection using the user or machine credentials." It is "supported on the Windows platform only. Its use on .NET Core on platforms other than Windows throws a PlatformNotSupportedException". It ships as the NuGet package `System.Security.Cryptography.ProtectedData` and is not in the base shared framework. `DataProtectionScope.CurrentUser` means "The result can be decrypted only by the same current user". There are `Protect`/`Unprotect` overloads with `optionalEntropy`.
- **Source URL:** https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata
- **Verification date:** 2026-10-25
- **Technical implications:** Any process running as the same Windows user can decrypt the data. DPAPI protects against other users and offline disk theft, not same-user malware. A fixed app-specific entropy only adds namespacing.
- **Confidence:** High
- **Limitations:** Same-user processes can read the secret. Roaming or profile resets can make the blob undecryptable (`CryptographicException`).
- **Selected implementation approach:** `ProtectedData.Protect(utf8Secret, entropy: "NinjaLauncher.v1"u8, DataProtectionScope.CurrentUser)`. Write to `%LOCALAPPDATA%\NinjaLauncher\accounts\<id>.bin` atomically (temp file plus `File.Move(overwrite: true)`). Guard with `[SupportedOSPlatform("windows")]` / `OperatingSystem.IsWindows()`. A `CryptographicException` on read means: delete the file and require sign-in.

### F6.2 macOS: Keychain via `/usr/bin/security`
- **Description:** From Apple's open-source `security` tool:
  - `add-generic-password [-a account] [-s service] [-w password] [-X hexpassword] [-U] [-T appPath] [keychain]`. `-U`: "Update item if it already exists (if omitted, the item cannot already exist)". `-X`: "password data to be added as a hexadecimal string".
  - `find-generic-password -a <account> -s <service> -w`, where `-w` means "Display only the password on stdout".
  - `delete-generic-password -a <account> -s <service>`.
  - Built-in usage warning: **"Use of the -p or -w options is insecure. Specify -w as the last option to be prompted."**
  - "By default, the application which creates an item is trusted to access its data without warning."
  - `security -i` runs interactive mode, reading commands from stdin (`readline`, max line length 4096 characters).
- **Source URL:** https://github.com/apple-oss-distributions/Security/blob/main/SecurityTool/macOS/security.c (usage strings, MAX_LINE_LEN) ; https://ss64.com/mac/security-password.html
- **Verification date:** 2026-10-25
- **Technical implications:**
  1. **Argument visibility:** a secret passed as `-w <secret>` or `-X <hex>` on the command line is visible to other processes (`ps`) while the command runs. Piping the command through `security -i` on stdin keeps the secret out of argv. The 4096-character line limit caps the secret at about 2000 characters when hex-encoded.
  2. **ACL caveat:** the item's creator, and therefore its trusted app, is `/usr/bin/security`, not the launcher. Any same-user process can read it silently with `security find-generic-password -w`. That is weaker than native `SecItemAdd` from a signed app (this is inferred from Apple's "creator is trusted" text; Medium).
  3. Reading with `-w` writes the secret to the child's stdout pipe, which is fine because the launcher owns that pipe.
- **Confidence:** High for the syntax and warnings. Medium for the ACL implication.
- **Limitations:** Long refresh tokens, or an MSAL cache blob larger than about 2 KB, will not fit the `-i` line limit. A locked keychain or headless session can trigger a GUI prompt or fail.
- **Selected implementation approach:**
  - **Write:** run `/usr/bin/security -i` (`ProcessStartInfo` with `ArgumentList`, no shell) and write one line to stdin, `add-generic-password -U -a "<accountId>" -s "dev.ninjalauncher.msa" -X <hex(utf8 secret)>`, then close stdin. Check the exit code. If hex(secret) plus the command exceeds 4000 characters, do not persist (F6.4).
  - **Read:** run `security find-generic-password -a <accountId> -s dev.ninjalauncher.msa -w` and trim the trailing newline.
  - **Delete:** run `security delete-generic-password -a <accountId> -s dev.ninjalauncher.msa`.
  - **Upgrade:** if the size cap or ACL weakness matters, switch to `Microsoft.Identity.Client.Extensions.Msal` (`WithMacKeyChain`, native Security.framework) or a P/Invoke to `SecItemAdd`.

### F6.3 Linux: Secret Service via `secret-tool` (libsecret)
- **Description:** `secret-tool store [--collection=...] --label='Label' {attribute} {value} ...` reads the secret from stdin when piped ("A password to store can also be piped in via stdin"; input runs until EOF and "any newline is kept as part of the password"). If run from a tty, it prompts for one line. `secret-tool lookup {attribute} {value} ...` prints the secret. `secret-tool clear {attribute} {value} ...` removes all matching unlocked items. Exit status: "On success 0 is returned, a non-zero failure code otherwise."
- **Source URL:** https://manpages.debian.org/testing/libsecret-tools/secret-tool.1.en.html
- **Verification date:** 2026-10-25
- **Technical implications:** The secret never appears in argv; only non-secret attributes do. `secret-tool` is in the `libsecret-tools` package, which may not be installed. It needs a running Secret Service (gnome-keyring, KWallet 5.97+ or KeePassXC with the integration enabled) on the session D-Bus. Flatpak/Snap sandboxes may block it.
- **Confidence:** High for the CLI behaviour. Medium for the provider list.
- **Limitations:** Headless or minimal window managers often have no Secret Service, and `store` then fails with a non-zero exit. A locked collection may pop an unlock prompt.
- **Selected implementation approach:**
  - **Store:** `secret-tool store --label="Ninja Launcher (<name>)" application dev.ninjalauncher account <accountId>`, writing the secret to stdin with **no trailing newline**, then close stdin. A non-zero exit means "unavailable" (F6.4).
  - **Lookup:** `secret-tool lookup application dev.ninjalauncher account <accountId>`.
  - **Clear:** `secret-tool clear application dev.ninjalauncher account <accountId>`.
  - **Probe availability:** check that `secret-tool` is on PATH, then do a store/lookup/clear round-trip with a dummy attribute at first use.

### F6.4 No secure store available
- **Description:** This is a policy decision, not a sourced fact. MSAL Extensions offers a plaintext "ACL-restricted" fallback (`WithUnprotectedFile`). This project rejects that option.
- **Source URL:** https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization (documents the fallback being rejected)
- **Verification date:** 2026-10-25
- **Technical implications:** The user must sign in on every launch on such systems.
- **Confidence:** High (design choice)
- **Limitations:** Worse user experience on headless or minimal Linux.
- **Selected implementation approach:** Keep the session **in memory only**. Show a persistent, explicit warning: "No secure credential store (Secret Service / Keychain) was found. You will need to sign in again next time. Install and unlock gnome-keyring or KWallet to stay signed in." **Never** write tokens to plaintext files, config, environment variables or logs.

---

## F7. Things the launcher must never do

- **Description and rationale:**
  1. **Never collect Microsoft passwords** or render Microsoft's sign-in page inside an app-controlled form or webview that could read keystrokes. Use the system browser only. MSAL docs: with server-rendered sign-in, "The password ... is never stored by the application". ROPC is something "Microsoft recommends you do not use" and does not work for personal accounts.
  2. **Never reuse Mojang's or Microsoft's official launcher client ID** (for example the legacy MSA ID `00000000402b5328` seen in community code; that ID is Medium confidence) or any other project's client ID. Mojang's review exists to stop impersonation and phishing ("some attempt to maliciously exploit our users through methods such as phishing attempts"). Use our own approved ID only.
  3. **Never log tokens.** That covers the auth code, PKCE verifier, MS access and refresh tokens, XBL/XSTS tokens, `uhs`, the Minecraft access token, and the full `identityToken` header. Redact `Authorization` headers and token-bearing JSON fields in HTTP logging. Redact `--accessToken` in any logged game command line. The game receives the token on its command line by design, which is unavoidable, so never echo that command line unredacted.
  4. Never ship a client secret. Never put tokens in URLs or query strings (other than the OAuth `code` the platform itself returns). Never store tokens in plaintext (F6.4).
  5. Never offer online play that bypasses authentication or ownership checks (stated as an approval blocker in F4). The registered app name must not contain Mojang, Minecraft, Microsoft, Live, Xbox, Discord or Hypixel.
- **Source URL:** https://help.minecraft.net/hc/en-us/articles/16254801392141 ; https://aka.ms/mce-reviewappid ; https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/using-web-browsers ; https://learn.microsoft.com/en-us/entra/identity-platform/msal-client-applications
- **Verification date:** 2026-10-25
- **Technical implications:** Add a logging redaction filter and a unit test that asserts no token-shaped strings reach log sinks.
- **Confidence:** High
- **Limitations:** The specific official client ID string is from community sources only.
- **Selected implementation approach:** As listed. Enforce via code review checklist and a redaction test.

---

## Implementation spec

Constants:
```
AUTHORITY      = https://login.microsoftonline.com/consumers
AUTHORIZE_URL  = https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize
TOKEN_URL      = https://login.microsoftonline.com/consumers/oauth2/v2.0/token
SCOPES         = XboxLive.signin offline_access
REDIRECT       = http://localhost:{ephemeralPort}      (registered as http://localhost)
CLIENT_ID      = <approved Ninja Launcher GUID, build config>
XBL_URL        = https://user.auth.xboxlive.com/user/authenticate
XSTS_URL       = https://xsts.auth.xboxlive.com/xsts/authorize
MC_LOGIN_URL   = https://api.minecraftservices.com/authentication/login_with_xbox
MC_ENT_URL     = https://api.minecraftservices.com/entitlements/mcstore
MC_PROFILE_URL = https://api.minecraftservices.com/minecraft/profile
```

### Step 1a: interactive sign-in (hand-rolled equivalent of MSAL `AcquireTokenInteractive`)
1. `verifier = base64url(random 32 bytes)`, `challenge = base64url(SHA256(verifier))`, `state = base64url(random 16 bytes)`.
2. Start a loopback listener on a free port (both loopbacks), giving `redirect = http://localhost:{port}`.
3. Open the system browser at:
```
GET {AUTHORIZE_URL}?client_id={CLIENT_ID}&response_type=code&redirect_uri={urlenc(redirect)}
   &response_mode=query&scope=XboxLive.signin%20offline_access&state={state}
   &code_challenge={challenge}&code_challenge_method=S256&prompt=select_account
```
4. Receive `GET /?code=...&state=...`. Reject a `state` mismatch. On `error=...`, surface `error_description`. Reply with a static HTML page and stop the listener. Time out after 5 minutes.
5. Exchange the code:
```
POST {TOKEN_URL}
Content-Type: application/x-www-form-urlencoded

client_id={CLIENT_ID}&grant_type=authorization_code&code={code}
&redirect_uri={urlenc(redirect)}&code_verifier={verifier}&scope=XboxLive.signin%20offline_access
```
   The response contains `access_token`, `expires_in`, `refresh_token`. On a 4xx response, the body has `error`, `error_description`, `error_codes`.

### Step 1b: silent refresh (each launch)
```
POST {TOKEN_URL}
Content-Type: application/x-www-form-urlencoded

client_id={CLIENT_ID}&grant_type=refresh_token&refresh_token={stored}&scope=XboxLive.signin%20offline_access
```
Store the returned `refresh_token`, replacing the old one. On `invalid_grant` or `interaction_required`, delete the stored token and go to Step 1a. With MSAL, Steps 1a and 1b are `AcquireTokenSilent` followed by a fallback to `AcquireTokenInteractive(["XboxLive.signin"])`.

### Step 2: Xbox Live
```
POST {XBL_URL}
Content-Type: application/json
Accept: application/json

{"Properties":{"AuthMethod":"RPS","SiteName":"user.auth.xboxlive.com","RpsTicket":"d={ms_access_token}"},
 "RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT"}
```
Read `xbl = .Token` and `uhs = .DisplayClaims.xui[0].uhs`.

### Step 3: XSTS
```
POST {XSTS_URL}
Content-Type: application/json
Accept: application/json

{"Properties":{"SandboxId":"RETAIL","UserTokens":["{xbl}"]},
 "RelyingParty":"rp://api.minecraftservices.com/","TokenType":"JWT"}
```
Read `xsts = .Token` and `uhs = .DisplayClaims.xui[0].uhs`. On 401, map `XErr` (long) per F2.3.

### Step 4: Minecraft login
```
POST {MC_LOGIN_URL}
Content-Type: application/json
Accept: application/json

{"identityToken":"XBL3.0 x={uhs};{xsts}"}
```
Read `mc = .access_token` and `mcExpiry = now + .expires_in`. Keep both in memory only. On 403, a body containing "Invalid app registration" means the app is not approved (owner blocker), `details.reason == ACCOUNT_SUSPENDED` means the account is suspended, and anything else surfaces `errorMessage`. Do not retry any 4xx.

### Step 5: Ownership
```
GET {MC_ENT_URL}
Authorization: Bearer {mc}
```
Owned if `items[].name` contains any of `game_minecraft`, `product_minecraft`, `product_game_pass_pc` or `product_game_pass_ultimate`.

### Step 6: Profile
```
GET {MC_PROFILE_URL}
Authorization: Bearer {mc}
```
200 gives `uuid = .id` and `name = .name`. 404 `NOT_FOUND` is handled per F3.3.

### Step 7: Launch
Pass `--username {name} --uuid {uuid} --accessToken {mc} --userType msa` to the game. Before launch, if `mcExpiry - now < 10 min`, repeat Steps 1b to 4. Redact `--accessToken` in logs.

### Persistence
Exactly one secret per account, `refresh_token` (or the MSAL cache blob), stored via `ICredentialStore`:
- Windows: DPAPI file (F6.1).
- macOS: `security -i` with `-X` (F6.2), or MSAL Extensions.
- Linux: `secret-tool` via stdin (F6.3).
- Otherwise: memory only with a warning (F6.4).

### Error UX table
| Condition | Message |
|---|---|
| Browser closed or timed out | "Sign-in was cancelled or timed out." |
| `access_denied` | "You declined the permission request." |
| XErr 2148916233 | "This Microsoft account has no Xbox profile. Sign in once at minecraft.net or xbox.com, then try again." |
| XErr 2148916238 | "Child account: an adult must add it to a Microsoft Family group." |
| XErr 2148916235 | "Xbox Live is not available in this account's region." |
| XErr 2148916227 | "This account is banned from Xbox Live." |
| login_with_xbox 403 "Invalid app registration" | "This build's app ID is not approved by Mojang (developer issue). See docs/research/AUTHENTICATION.md F4." |
| No entitlements | "This account does not own Minecraft: Java Edition." |

---

## Blockers for owner

1. **Create the Azure app registration** (F1.6). Steps:
   - Get an Azure account with a tenant. Microsoft states "Create an account for free"; any verification requirements were not checked.
   - In Entra admin center > App registrations > New registration, name it **"Ninja Launcher"** (no Minecraft, Mojang, Microsoft, Xbox, Live, Discord or Hypixel in the name).
   - Account type: personal accounts.
   - Platform "Mobile and desktop applications", redirect `http://localhost`.
   - No secret. "Allow public client flows" = No unless device code is wanted.
   - Record the **Application (client) ID** and **Directory (tenant) ID**.
2. **Submit the Mojang AppID Review** at **https://aka.ms/mce-reviewappid**:
   - Confirm the EULA and Usage Guidelines (http://aka.ms/mcusageguidelines).
   - Give the same contact email as the Azure account (they cross-check it).
   - Request type "New AppID for Approval".
   - Provide the app name, client ID, tenant ID and the project website or repo URL.
   - Justification: an open-source GPL launcher that uses Microsoft sign-in through the system browser, verifies ownership via entitlements and profile, never bypasses authentication, and stores tokens in the OS secure store.
   - Submit **once**: reviews are weekly and duplicates do not help. Questions go to `enforce@minecraft.net`.
3. **Until approval arrives**, `login_with_xbox` returns 403. Development can complete and test Steps 1 to 3 (Microsoft, XBL, XSTS) and use mocked responses for Steps 4 to 6. Using another launcher's client ID to test is not allowed (F7).
4. **Fork policy decision:** document that forks must register and get approval for their own client ID. The client ID is a build-time setting, not a secret.
5. **Unverified items to confirm during the first spike:**
   - That the `XboxLive.signin` token from MSAL works as `RpsTicket` `d=`.
   - The exact JSON of the 403 "Invalid app registration" response.
   - Whether Xbox endpoints need TLS renegotiation on Linux/macOS .NET.
   - The macOS refresh-token size against the 4096-character `security -i` line limit.
   - The claim in the Microsoft Q&A community answer that ID@Xbox enrolment is required. The official Mojang page does not say this.
