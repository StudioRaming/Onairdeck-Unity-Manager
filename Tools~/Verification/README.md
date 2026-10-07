# Download verification

## Filesystem checks (no Unity license required)

From the package repository:

```powershell
dotnet run --project 'Tools~/Verification/Verification.csproj'
```

The 14 checks compile the actual `Installer` and `PathSafety` sources. A small host adapter supplies only `Application.dataPath` and a warning sink. Tests create a fresh temporary project, check file contents, overwrite behavior and `.meta` preservation, ZIP nesting, unsafe-entry rejection and the download destination, then delete only that run's temporary directory.

This does not exercise Unity requests, imports or script reloads.

## Real Editor checks

Use a disposable Unity project when batch licensing is available. Otherwise the same checks can run in an open test project: fixtures use a unique `Assets/StudioRaming_Onairdeck/__Verification_<id>/` folder and do not modify scenes or seller data.

1. Install the local manager package. For a new project, copy `PackageBootstrap.cs` into `Assets/Editor/` and launch the Editor with `-executeMethod OnAirDeckPackageBootstrap.Install -onairdeckPackage <package-directory>`, without `-quit`. The script installs via the Package Manager API and exits when complete.
2. Run `python Tools~/Verification/make_import_fixtures.py <test-project-directory>`. It creates two tiny `.unitypackage` archives in that project's Library cache: a script that forces a domain reload and a text asset queued behind it. Archives contain only the unique test assets, preserving existing parent-folder metadata.
3. Copy `EditorVerification.cs` into the test project's `Assets/Editor/`. In batch mode, launch with `-executeMethod OnAirDeckEditorVerification.Run` without `-quit`; the runner exits itself. In an open Editor, create the empty file `Library/OnAirDeckVerification/run.request` before recompilation, then focus Unity once. The one-shot runner leaves the open Editor running.
4. Read `Library/OnAirDeckVerification/results.json`. A passing report verifies a loose file at the real Unity destination, ZIP extraction, UnityWebRequest byte contents, cancellation/failure cache cleanup, two queued imports across a script reload, and import-cache cleanup. Requests use localhost; no paid downloads or purchase records are changed.
5. Remove the temporary verification script and its `.meta`, and the specific `__Verification_<id>` fixture folder named in `fixtures.json`. In an open Editor, use the Project window to delete the fixture folder so Unity updates its asset database.

Package imports are noninteractive for verification; regular manager downloads continue to show the import dialog. The queue persists this setting across script reloads.

The editor runner also supports Unity 2022.3. Unity 2019.4 and Unity 6 still need a version-specific install/compatibility check before public release.
