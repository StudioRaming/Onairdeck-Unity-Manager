# OnAirDeck Unity Manager

A Unity Editor tool for OnAirDeck buyers:

- sign in and sign out through the browser,
- see your OnAirDeck purchase history,
- download purchased assets straight into the open project.

> Status: Phase 1 feature-complete (sign in, purchase list, download); being tested before the first public release.

## Requirements

Unity 2019.4 or newer (tested first on 2021.3.18f1).

## Install

In Unity, open **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and enter:

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

The repository is private during development, so this only works for accounts with access until the first public release.

## Use

1. Open **Window ▸ OnAirDeck Unity Manager** and click **Sign in with browser**. Approve the request on onairdeck.com. The sign-in is saved per computer, so it applies to every Unity project, and lasts 30 days unless you sign out.
2. Your purchases are listed with the files the seller made available for Unity. Items the seller has not enabled for Unity say "Not available in Unity".
3. Click **Download**. The first download of an item makes the purchase non-refundable (the same rule as the website), so Unity asks you to confirm.
   - `.unitypackage` files open Unity's normal import dialog.
   - `.zip` files are extracted, and other files are copied, into `Assets/StudioRaming/<Product>/`.
4. **Update available** appears when the seller replaced a file after your last download.

## Test a local copy (development)

In any Unity project: **Window ▸ Package Manager ▸ + ▸ Add package from disk…** and select this folder's `package.json`. Changes to the files are picked up when Unity recompiles.

To compile-check without opening Unity, run `dotnet build` in `Tools~/CompileCheck` (instructions inside the `.csproj`).
