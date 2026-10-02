# OnAirDeck Unity Manager

A Unity Editor tool for OnAirDeck buyers:

- sign in and sign out through the browser,
- see your OnAirDeck purchase history,
- download purchased assets straight into the open project.

> Status: in development (Phase 1). Sign in / sign out is implemented and being tested; purchase history and downloads come next.

## Requirements

Unity 2019.4 or newer (tested first on 2021.3.18f1).

## Install

In Unity, open **Window ▸ Package Manager ▸ + ▸ Add package from git URL…** and enter:

```
https://github.com/StudioRaming/Onairdeck-Unity-Manager.git
```

The repository is private during development, so this only works for accounts with access until the first public release.

## Use

Open **Window ▸ OnAirDeck Unity Manager** and click **Sign in with browser**. Approve the request on onairdeck.com, and the window shows the account you signed in with. The sign-in is saved per computer, so it applies to every Unity project, and lasts 30 days unless you sign out.

## Test a local copy (development)

In any Unity project: **Window ▸ Package Manager ▸ + ▸ Add package from disk…** and select this folder's `package.json`. Changes to the files are picked up when Unity recompiles.

To compile-check without opening Unity, run `dotnet build` in `Tools~/CompileCheck` (instructions inside the `.csproj`).
