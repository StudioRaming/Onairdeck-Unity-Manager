# Changelog

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
