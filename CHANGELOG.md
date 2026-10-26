# Changelog

## [0.5.0] - 2026-10-25

- **Packages section.** `plugin-purchases` now also returns the seller licenses the account may download (`licenses`, each with its versions). For versions whose file name is `<package>-<version>.zip` the window shows, per package, the installed version (from `Packages/<name>/package.json`), the newest version, **Install** / **Update** and a picker for older versions. `plugin-download` accepts `license_version_id` for these (`OnAirDeckApi.RequestLicenseVersionDownloadAsync`).
- **One install transaction** for a package and its exact dependencies (`Editor/Packages/`). Zips are validated (any unsafe entry fails the whole package; root `package.json` name and version must equal the listing; `vpmDependencies` must pin exact versions as `"X"` or `"=X"`, copied verbatim into `locked.<name>.dependencies`) and dependencies come only from a fixed list of trusted VPM listings (the stable MingToon listing, then the preview listing, the only one with prerelease versions), verified by SHA-256. The zips are staged under `Temp/OnAirDeck/`, a journal is written to `Library/OnAirDeck/install-journal.json`, the contents of each existing `Packages/<name>` go to `Library/OnAirDeck/Backup/<name>-<oldVersion>-<time>` and the new contents are moved in (the folder itself stays; a package not installed before arrives as a whole folder). A failure undoes the moves in reverse order; an interrupted install is rolled back on the next editor load. Files the new package does not contain are not carried over and are listed in the result. Existing entries of `Packages/vpm-manifest.json` are updated (backed up first, nothing added, unknown keys kept), then `Client.Resolve()` and `AssetDatabase.Refresh()` run once. A package installed the old way under `Assets/` (MingToon: `Assets/StudioRaming/MingToon`) is refused until it is migrated.
- **GUID collision guard.** Before any move the `.meta` GUIDs of every zip are matched against `Assets/` and the embedded package folders (not the ones being replaced). Stray files inside an installed package, such as an old add-on `.unitypackage` import inside the core folder, are removed by reinstalling that package at its current version from a trusted listing in the same transaction (reported, kept in the backup). Any other collision refuses the install before any change, listing up to 10 paths. Two zips of one install that share a GUID are refused as well.
- **Mismatch banner** when an installed package's exact dependencies disagree with the installed dependency version; its button names the versions it will install.
- **`OnAirDeckPackages.OpenAndFocus(packageName)`**, a public entry point other packages can call to open the window on their own row.
- **Git-free install and self-update.** `Tools~/Packaging/build_manager_packages.py` builds `OnAirDeck-Unity-Manager-<version>.unitypackage` and `com.studioraming.manager-<version>.zip` and self-checks them. An embedded Manager updates itself from the release's `download_url` through the same validation and transaction; git and registry installs keep the Package Manager update. `VersionResponse` also reads a top-level `download_url`.
- `OnAirDeck.License` lets the Manager assembly use its internal `MiniJson` (`InternalsVisibleTo`).
- **Held files during the swap.** Windows refuses to rename a folder while a file inside it is open, and antivirus briefly opens freshly extracted files (with Defender on, 8 of 80 MingToon add-on extractions failed the first move; each cleared within 135 ms). Every folder move of the transaction and its rollback now retries such a lock for up to 5 seconds; a missing source, a destination in the way or a move across volumes still fails at once. Found by the URP update test bed, where the first add-on install failed and rolled back.
- **Updates keep the package folder.** Unity watches each embedded package folder and keeps watching it after a rename. The first update test on Unity 2021.3.45f2 renamed the old MingToon folders to the backup and put new ones in their place; the next `AssetDatabase.Refresh` (and Unity's own refresh after `Client.Resolve`) then waited forever in `DirectoryMonitorWindows::Sync`, with the editor frozen at no CPU. A small repro project showed the same freeze for that pattern only: moving contents, deleting the folder, or renaming it away with nothing in its place all refresh normally. An update now moves the old contents to the backup and the new contents in, and the rollback does the reverse, keeping the folder. The journal (format 2) records which contents the folder holds so a crash between any two moves is undone; an unfinished journal of the older format is set aside with a message instead of replayed.
- **Journal hardening.** The journal is written to a temporary file and then put in place in one replace, so a crash while it is saved leaves the previous journal, never part of one (it used to be copied over the old file). Recovery refuses an unfinished journal whose package name is not a package name, whose stage folder is empty, or whose entries lack a string `phase`; and when a journal says a package was not touched while that package's backup holds files, recovery moves nothing and leaves the journal unfinished with a message to check by hand.
- **Window copy.** A BOOTH-claim license with packages but no OnAirDeck order no longer shows "No purchases yet" under its own packages. The install confirmation states the refund rule only for licenses bought on onairdeck.com. A message (an install report, for one) has a **Dismiss** button, and a separator sets the **Open onairdeck.com** button apart from it. Full-width buttons wrap their label, so the button that fixes a version mismatch no longer cuts off both package names in the narrowest window.
- 29 more headless checks (65 total), including GUID collision cases: zip validation cases, SHA-256 and HTTPS gates, the listing lookup, the transaction with a failure injected and a crash simulated at every fault point (each journal save included), rollback retry and a rollback crashed at every fault point, a file held open during the swap, a watcher on the package folder that must still see new files after an update, a rollback and a recovery, the vpm-manifest edit, the legacy refusal, tampered journals (one field changed per case, each refused for its own reason), a journal that contradicts the backup, the mismatch finder.

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
