# Changelog

## [0.4.0] - 2026-10-15

- **Runtime license check for sellers.** New assembly `OnAirDeck.License` (folder `License/`, Editor-only, no UnityEngine dependency): `OnAirDeckLicense.Check("<product id>")` finds the product's `onairdeck.license` under `Assets/StudioRaming_Onairdeck/`, verifies the RSA signature with OnAirDeck's embedded public key, and checks the product, the signed-in account (the Manager's session marker) and the expiry, all offline. The result carries a `LicenseStatus` reason (`NotSignedIn`, `LicenseMissing`, `InvalidSignature`, `Expired`, `UserMismatch`, ...) and a message, so a seller's tool can say "sign in with OnAirDeck Unity Manager" instead of failing silently. Results are cached for 30 seconds so the check can run from `OnGUI`. `LicenseVerifier.Verify` exposes the pure verification for custom setups.
- After writing a license the Manager runs the same check and logs a warning if the file would not pass it, so a key or format mismatch shows up immediately.
- Seven more headless verification checks (35 total): a license issued by the real service verified with the embedded key, tampering, malformed input, JSON escapes, and the file/marker lookup with its cache.
- Honest limit, unchanged: a client-side check deters sharing; it cannot stop someone who decompiles and edits the calling assembly.

## [0.3.0] - 2026-10-15

- **License file.** After a download the Manager asks OnAirDeck for a signed license and writes `onairdeck.license` into the product folder (`Assets/StudioRaming_Onairdeck/<Product>/`). Licenses live 7 days; the Manager re-issues them silently for installed products when the window opens and the file is missing or within 2 days of expiry. Issuing a license never counts as a download.
- **Session marker.** While signed in, `%LOCALAPPDATA%/OnAirDeck/session.json` holds the signed-in user id (never the token). Removed on sign-out and when the server rejects the session. Together with the license file it lets a seller's asset verify "signed in + licensed" without network access.
- **Update check.** On window open the Manager asks `plugin-version` once per Editor session. A newer published version shows an **Update** button (git/registry installs switch to the published git URL through Package Manager; local-disk installs are told to update their copy). A version below the server's minimum blocks sign-in until updated.
- Four more headless verification checks (license parsing, refresh window, write confinement, session marker).

## [0.2.4] - 2026-10-08

- Minimum Unity version raised to 2021.3 (`package.json`), matching what has been tested; older editors can no longer install the package by mistake. No code changes.
- Documentation describes the supported range honestly and adds a git-URL installation check to the release list.

## [0.2.3] - 2026-10-08

- Sign-in verification: 10 headless checks drive the real localhost listener (approved code, Deny, state mismatch, missing code, timeout, Cancel, favicon requests, port release) plus PKCE and session-expiry checks; the suite now has 24 checks and runs in CI.
- The Editor runner also checks saved-session handling (expired, malformed and valid sign-ins, a rejected token, sign-out) against a throwaway EditorPrefs key and confirms the real sign-in is unchanged.
- `SavedSession` moved to its own file so it can be tested outside Unity. `SessionStore.Load/Clear` and `AuthService.SignOutAsync` gained internal key overloads used only by verification; the window and normal sign-in/out always use the same fixed EditorPrefs key, so a verification run never redirects the real session.

## [0.2.2] - 2026-10-07

- Skip ZIP entries with rooted paths, matching the existing safe-extraction contract.
- Package import queues support noninteractive verification and retain their interaction mode through script reloads. Normal downloads still show Unity's import dialog.
- Repeatable filesystem checks and a real Editor verification runner for downloading, cancellation, cache cleanup and queued imports across script reloads.

## [0.2.1] - 2026-10-07

- Buttons use a consistent editor style and height, with widths measured from their labels, including Download again, Downloading, Loading and Signing out.
- Product names and purchase details wrap in narrow windows instead of competing with the download button.
- Loose files and ZIPs now install under `Assets/StudioRaming_Onairdeck/<Product>/`. Existing downloads stay in their previous folders. Unity packages still use Unity's import dialog and their original asset paths.
- English, Korean and Japanese installation/use instructions and a release verification checklist.

## [0.2.0] - 2026-10-06

- Purchase list: every OnAirDeck purchase with its Unity files, search, Refresh, Downloaded / Update available badges; items without Unity files show "Not available in Unity".
- Download: files stream to Library/OnAirDeckCache, then .unitypackage files open Unity's import dialog (queued one at a time, surviving script reloads), zips are extracted and other files copied under Assets/StudioRaming/<Product>/. Server-supplied names and zip entries are sanitised; traversal entries are skipped.
- The first download of an item asks for confirmation because it makes the purchase non-refundable.

## [0.1.0] - 2026-10-02

- Window ▸ OnAirDeck Unity Manager: sign in through the browser (PKCE, localhost callback) and sign out (the session is revoked on the server).
- Sign-in is saved in EditorPrefs for 30 days and shared by all projects on this computer.

## [0.0.1] - 2026-10-02

- Repository and package skeleton.
