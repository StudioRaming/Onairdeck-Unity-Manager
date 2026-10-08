# Download verification

## Headless checks (no Unity license required)

From the package repository:

```powershell
dotnet run --project 'Tools~/Verification/Verification.csproj'
```

The 24 checks compile the actual `Installer`, `PathSafety`, `LoopbackListener`, `Pkce` and `SavedSession` sources. A small host adapter supplies only `Application.dataPath` and a warning sink.

- **Files (14):** a fresh temporary project; file contents, overwrite behavior and `.meta` preservation, ZIP nesting, unsafe-entry rejection and the download destination. Only that run's temporary directory is deleted.
- **Sign-in (10, `AuthChecks.cs`):** the real localhost listener is driven the way a browser would drive it: approved code, `/favicon.ico` noise, **Deny** (`error=access_denied`), state mismatch, missing code, **timeout**, **Cancel**, port release after dispose; PKCE verifier/S256 challenge shape; saved-session expiry boundary. The OnAirDeck website is not contacted.

This does not exercise Unity requests, EditorPrefs, imports or script reloads.

## Real Editor checks

Use a disposable Unity project when batch licensing is available. Otherwise the same checks can run in an open test project: fixtures use a unique `Assets/StudioRaming_Onairdeck/__Verification_<id>/` folder and do not modify scenes or seller data.

1. Install the local manager package. For a new project, copy `PackageBootstrap.cs` into `Assets/Editor/` and launch the Editor with `-executeMethod OnAirDeckPackageBootstrap.Install -onairdeckPackage <package-directory>`, without `-quit`. The script installs via the Package Manager API and exits when complete.
2. Run `python Tools~/Verification/make_import_fixtures.py <test-project-directory>`. It prepares a unique fixture manifest. The Editor runner then uses Unity's own exporter to create two tiny packages in the project's Library cache. Text sources avoid a reload during preparation; the runner rewrites only their target paths and the script importer's metadata, preserving Unity's archive headers. The first import compiles a marker script and causes a domain reload, with a text asset queued behind it. Packages contain only unique test assets and preserve existing parent-folder metadata.
3. Copy `EditorVerification.cs` into the test project's `Assets/Editor/`. In batch mode, launch with `-executeMethod OnAirDeckEditorVerification.Run` without `-quit`; the runner exits itself. In an open Editor, create the empty file `Library/OnAirDeckVerification/run.request` before recompilation, then focus Unity once. The one-shot runner leaves the open Editor running.
4. Read `Library/OnAirDeckVerification/results.json`. A passing report first verifies saved-session handling — expired, malformed and valid sign-ins, a rejected token (401) surfacing as an expired sign-in, and sign-out clearing the saved sign-in — using a throwaway EditorPrefs key, then asserts the real saved sign-in is unchanged. The rejected-token check sends a random token to `plugin-purchases` (expected 401; nothing is changed server-side). It then verifies a loose file at the real Unity destination, ZIP extraction, UnityWebRequest byte contents, cancellation/failure cache cleanup, two queued imports across a script reload, and import-cache cleanup. Requests use localhost; no paid downloads or purchase records are changed.
5. Remove the temporary verification script and its `.meta`, and the specific `__Verification_<id>` fixture folder named in `fixtures.json`. In an open Editor, use the Project window to delete the fixture folder so Unity updates its asset database.

Package imports are noninteractive for verification; regular manager downloads continue to show the import dialog. The queue persists this setting across script reloads.

The package's minimum is Unity 2021.3 for now; the editor runner should also work on 2022.3, but 2022.3 and Unity 6 are unverified and not yet claimed as supported.

## Verified on 2026-10-08

- Headless suite: **24 passed** (14 file checks + 10 sign-in checks), locally and in CI.
- Real Unity **2021.3.18f1**, open-Editor workflow: **13 checks passed** — 6 saved-session/401/sign-out checks (real sign-in confirmed unchanged) plus the 7 download/import checks, with **1 script domain reload**. Report kept outside the repo.
- Owner checks in the same Editor: Deny on the real consent page, button layout at minimum width, and a real download into `Assets/StudioRaming_Onairdeck/test_sell/`.

## Verified on 2026-10-07

- Filesystem suite: **14 passed**, locally and in Windows GitHub Actions.
- Real Unity **2021.3.18f1**: **7 checks passed**, including request cancellation/failure cleanup and two queued package imports across **1 script domain reload**. No purchase records changed.
- Both compatibility compile paths pass against the installed Unity 2021.3.18f1 assemblies.
- Unity 2021 batch launches on this PC still reject licensing after CLI sign-in/activation; use the open-Editor request-file workflow above until batch launching is resolved.
