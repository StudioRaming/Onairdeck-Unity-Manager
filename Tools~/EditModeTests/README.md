# EditMode tests (real Editor)

Not shipped: Unity ignores folders ending in `~`, and `build_manager_packages.py` leaves `Tools~` out. To run them, put the
package in a test project (embedded under `Packages/com.studioraming.manager`, or added from disk), copy or link this folder
into the project's `Assets/` (for example `Assets/ManagerEditModeTests`), add `com.unity.test-framework` to the manifest, and run

```
Unity.exe -batchmode -projectPath <project> -runTests -testPlatform EditMode -testResults <results.xml> -testFilter OnAirDeck.UnityManager.EditModeTests
```

The Manager assembly grants these tests its internals (`Editor/AssemblyInfo.cs`). Each test dismisses the error cards it made;
dismissed signatures go to the test project's `Library/OnAirDeck/InstallErrorReports.txt`.
