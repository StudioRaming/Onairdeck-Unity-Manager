# OnAirDeck Unity Manager — working notes

This folder is the Unity package repo **StudioRaming/Onairdeck-Unity-Manager** (GitHub, owner account StudioRaming / studioraming@gmail.com). Package id `com.studioraming.manager`.

## Which repo gets which change

- **Unity-side files** (C#, asmdef, package.json, .meta, Unity test project, Unity docs) → this repo.
- **Everything else** (edge functions, migrations, Atelier seller UI, i18n, the overall plan) → the Vdesk repo, checked out as a sibling folder (`..\Vdesk`).
- The overall plan lives in Vdesk: `docs/plans/2026-10-02-unity-manager-phase1.md`.

## Git identity

Commits and pushes must be StudioRaming. The global gitconfig on this PC is a different account on purpose, so this repo has its own local `user.name`/`user.email`/`credential.https://github.com.username`. Push without the Git Credential Manager prompt:

```
git -c credential.helper= -c 'credential.helper=!f() { echo username=StudioRaming; echo "password=$GITHUB_TOKEN"; }; f' push
```

## Code rules

- Supported range **for now: Unity 2021.3 or newer** (`package.json` `"unity": "2021.3"`), tested on 2021.3.18f1 (installed through Unity Hub). Keep the code C# 7.3 with the pre-2020.2 fallbacks anyway so older editors can be re-enabled later without a rewrite.
  - C# 7.3 only: no `using var`, switch expressions, `??=`, nullable reference types, records, or ranges.
  - `UnityWebRequest.result` exists only from 2020.2. Use `#if UNITY_2020_2_OR_NEWER`, else `isNetworkError`/`isHttpError`.
  - No UniTask or Newtonsoft dependency. Use `JsonUtility` and editor-loop polling or `async`/`await` (the editor has a main-thread SynchronizationContext).
  - Zip: `System.IO.Compression.ZipArchive` over a `FileStream`, not `ZipFile`.
- Editor-only code: everything lives under `Editor/` with an Editor-only asmdef.
- Every file and folder in the package needs a committed `.meta` (git packages are immutable; Unity ignores assets without one). New files: write a `.meta` with a fresh random 32-hex `guid` (MonoImporter for .cs, AssemblyDefinitionImporter for .asmdef, folderAsset for folders, TextScriptImporter for .md) — or let Unity create them by adding the package from disk in a test project.
- **Compile check without Unity:** `dotnet build` in `Tools~/CompileCheck` (C# 7.3 against the 2021.3.18f1 DLLs; both code paths documented in the .csproj). Unity batch mode does NOT work here unless Unity Hub is signed in ("Access token is unavailable" → license failure), so the dotnet check is the default; the user still tests interactively in Unity.
- Sanitize every server-supplied file name or zip entry before writing under `Assets/` (no `..`, rooted, or drive paths).
- Backend endpoints: `https://sptkmcrpgdvoegbfzqqq.supabase.co/functions/v1/plugin-*` with the public publishable key as `apikey`, and the opaque session token as `Authorization: Bearer`. Login page: `https://onairdeck.com/plugin-auth`.
- Reference implementation: the Warudo plugin in the sibling `..\Warudo_sr_plugin_fix` checkout, `Assets\StudioRaming\StudioRamingPlugin.cs` (sign-in) and `StudioRamingPlugin.Vdesk.Download.cs` (download).
