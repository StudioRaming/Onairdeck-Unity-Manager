using System;
using System.IO;
using System.Linq;
using OnAirDeck.UnityManager;

// The self-update (owner decision 2026-10-31): when to ask, when to install, how a reload settles the record, the note, which compile
// errors trigger the rollback, and the rollback of an embedded update.
internal static class AutoUpdateChecks
{
    public static void Run(Action<string, Action> check, string project)
    {
        check("auto update timing: asked on open unless asked within 10 minutes, once a day while open, at once when never asked or the clock went back", Timing);
        check("auto update decision: only a release newer than the running code that this install can take; never one being installed, rolled back or failed here; waits while an install, import, compile or play mode runs", Decision);
        check("auto update settle: the new version marks the update done; the old one marks it failed, or deferred when it was cut before any swap", Settle);
        check("auto update note: 'updated' once in the new version, 'rolled-back' in the old one, nothing while applying or after it was seen", Note);
        check("auto update rollback trigger: an error in a package the update swapped (by its logical Packages/<name>/ path, also on the git route), never before the swap is recorded; an update in flight blocks every other", ErrorScope(project));
        check("auto update record: written and read back with every field and every swapped folder; a missing or broken file reads as none", Record(project));
        check("auto update rollback: every swapped folder comes back (last first) with vpm-manifest.json, a folder the update added leaves Packages/, the failed files are kept, a resumed rollback skips folders already back; nothing recorded or a missing or empty backup refuses before anything moves", Rollback(project));
        check("auto update rollback: a move that fails mid-way puts the folder back as it was before the restore, and says so", RollbackUndo(project));
        check("auto update rollback: cut after the new files left a folder and some old ones came back, a resume read from disk brings only the rest of the backup", ResumeFromDisk(project));
        check("auto update record: a record that is there but cannot be read counts as unreadable (nothing may start over it), a missing one does not", () =>
        {
            var root = Path.Combine(project, "autoupdate-unreadable");
            Directory.CreateDirectory(Path.Combine(root, "Library", "OnAirDeck"));
            Require(!AutoUpdateLogic.Unreadable(root));
            File.WriteAllText(AutoUpdateLogic.RecordPath(root), "{ broken");
            Require(AutoUpdateLogic.Unreadable(root));
            AutoUpdateLogic.Save(root, new SelfUpdateRecord { Phase = SelfUpdateRecord.PhaseShown });
            Require(!AutoUpdateLogic.Unreadable(root));
        });
        check("auto update pref key: the project hash is the same in every run and ignores case", () =>
        {
            Equal(AutoUpdateLogic.StableHash("C:/Proj/A"), AutoUpdateLogic.StableHash("c:/proj/a"));
            Equal("811c9dc5", AutoUpdateLogic.StableHash(""));
            Require(AutoUpdateLogic.StableHash("C:/Proj/A") != AutoUpdateLogic.StableHash("C:/Proj/B"));
        });
    }

    private static void Timing()
    {
        const long t = 2000000000;
        Require(AutoUpdateLogic.CheckDue(true, t, 0));
        Require(!AutoUpdateLogic.CheckDue(true, t, t - 5 * 60));
        Require(AutoUpdateLogic.CheckDue(true, t, t - 10 * 60));
        Require(!AutoUpdateLogic.CheckDue(false, t, t - 23 * 3600));
        Require(AutoUpdateLogic.CheckDue(false, t, t - 24 * 3600));
        Require(AutoUpdateLogic.CheckDue(false, t, t + 60));   // the clock went back
    }

    private static void Decision()
    {
        var safe = new AutoUpdateMoment();
        Equal(AutoUpdateAction.Apply, AutoUpdateLogic.Decide(true, true, "0.1.5", "0.1.4", "", safe));
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(false, true, "0.1.5", "0.1.4", "", safe));
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(true, false, "0.1.5", "0.1.4", "", safe));   // git working copy, local disk, no URL
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(true, true, "", "0.1.4", "", safe));
        // The server can never move the Manager back or sideways, whatever update_available says.
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(true, true, "0.1.4", "0.1.4", "", safe));
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(true, true, "0.1.3", "0.1.4", "", safe));
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(true, true, "0.1.5-preview.1", "0.1.5", "", safe));
        Equal(AutoUpdateAction.Blocked, AutoUpdateLogic.Decide(true, true, "0.1.5", "0.1.4", "0.1.5", safe));
        Equal(AutoUpdateAction.Apply, AutoUpdateLogic.Decide(true, true, "0.1.6", "0.1.4", "0.1.5", safe));   // a later release is tried again
        foreach (var busy in new[]
        {
            new AutoUpdateMoment { WindowBusy = true }, new AutoUpdateMoment { Compiling = true }, new AutoUpdateMoment { Playing = true },
            new AutoUpdateMoment { ImportsQueued = true }, new AutoUpdateMoment { ImportRequest = true },
        })
        {
            Equal(AutoUpdateAction.Wait, AutoUpdateLogic.Decide(true, true, "0.1.5", "0.1.4", "", busy));
            Require(busy.Reason() != null);
        }
        Require(safe.Reason() == null);

        // Blocked while it is being installed or rolled back, and after it failed; free again once done, deferred or shown.
        var record = new SelfUpdateRecord { To = "0.1.5" };
        foreach (var phase in new[] { SelfUpdateRecord.PhaseApplying, SelfUpdateRecord.PhaseRestoring, SelfUpdateRecord.PhaseRolledBack, SelfUpdateRecord.PhaseFailed })
        {
            record.Phase = phase;
            Equal("0.1.5", AutoUpdateLogic.BlockedVersion(record));
        }
        foreach (var phase in new[] { SelfUpdateRecord.PhaseApplied, SelfUpdateRecord.PhaseDeferred, SelfUpdateRecord.PhaseShown })
        {
            record.Phase = phase;
            Equal("", AutoUpdateLogic.BlockedVersion(record));
        }
        Equal("", AutoUpdateLogic.BlockedVersion(null));
    }

    private static void Settle()
    {
        var record = new SelfUpdateRecord { From = "0.1.4", To = "0.1.5", Route = "embedded", Phase = SelfUpdateRecord.PhaseApplying };
        Require(AutoUpdateLogic.Settle(record, "0.1.5"));
        Equal(SelfUpdateRecord.PhaseApplied, record.Phase);
        Require(!AutoUpdateLogic.Settle(record, "0.1.5"));   // only an applying record settles

        record = new SelfUpdateRecord { From = "0.1.4", To = "0.1.5", Route = "embedded", Phase = SelfUpdateRecord.PhaseApplying };
        Require(AutoUpdateLogic.Settle(record, "0.1.4"));
        Equal(SelfUpdateRecord.PhaseDeferred, record.Phase);   // no swap recorded: nothing changed, try again later

        record = new SelfUpdateRecord { From = "0.1.4", To = "0.1.5", Route = "embedded", Phase = SelfUpdateRecord.PhaseApplying };
        record.Swaps.Add(new SelfUpdateSwap { Target = "x", Backup = "y" });
        Require(AutoUpdateLogic.Settle(record, "0.1.4"));
        Equal(SelfUpdateRecord.PhaseFailed, record.Phase);

        record = new SelfUpdateRecord { From = "0.1.4", To = "0.1.5", Route = "git", Phase = SelfUpdateRecord.PhaseApplying };
        Require(AutoUpdateLogic.Settle(record, "0.1.4"));
        Equal(SelfUpdateRecord.PhaseFailed, record.Phase);
    }

    private static void Note()
    {
        var record = new SelfUpdateRecord { From = "0.1.4", To = "0.1.5", Phase = SelfUpdateRecord.PhaseApplied };
        Equal("updated", AutoUpdateLogic.NoteFor(record, "0.1.5"));
        Equal(null, AutoUpdateLogic.NoteFor(record, "0.1.4"));
        record.Phase = SelfUpdateRecord.PhaseRolledBack;
        Equal("rolled-back", AutoUpdateLogic.NoteFor(record, "0.1.4"));
        Equal(null, AutoUpdateLogic.NoteFor(record, "0.1.5"));
        foreach (var phase in new[] { SelfUpdateRecord.PhaseApplying, SelfUpdateRecord.PhaseShown, SelfUpdateRecord.PhaseFailed, SelfUpdateRecord.PhaseDeferred })
        {
            record.Phase = phase;
            Equal(null, AutoUpdateLogic.NoteFor(record, "0.1.5"));
            Equal(null, AutoUpdateLogic.NoteFor(record, "0.1.4"));
        }
        Equal(null, AutoUpdateLogic.NoteFor(null, "0.1.4"));
    }

    private static Action ErrorScope(string parent)
    {
        return () =>
        {
            var root = Path.Combine(parent, "autoupdate-scope");
            var manager = Path.Combine(root, "Packages", "com.studioraming.manager");
            var record = new SelfUpdateRecord { Route = "embedded", PackageFolder = manager, PackageName = "com.studioraming.manager" };
            // Before the swap is recorded nothing of the update is on disk: an error in the Manager's files is its own.
            Require(!AutoUpdateLogic.ErrorInUpdate(record, root, "Packages/com.studioraming.manager/Editor/A.cs"));
            record.Swaps.Add(new SelfUpdateSwap { Name = "com.studioraming.manager", Target = manager });
            record.Swaps.Add(new SelfUpdateSwap { Name = "com.studioraming.helper", Target = Path.Combine(root, "Packages", "com.studioraming.helper") });
            Require(AutoUpdateLogic.ErrorInUpdate(record, root, "Packages/com.studioraming.manager/Editor/NewThing/Broken.cs"));   // a new asmdef too
            Require(AutoUpdateLogic.ErrorInUpdate(record, root, Path.Combine(root, "Packages", "com.studioraming.helper", "Runtime", "A.cs")));
            Require(!AutoUpdateLogic.ErrorInUpdate(record, root, "Assets/Scripts/UserCode.cs"));
            Require(!AutoUpdateLogic.ErrorInUpdate(record, root, "Packages/com.studioraming.manager-old/Editor/A.cs"));   // a sibling, not inside
            Require(!AutoUpdateLogic.ErrorInUpdate(record, root, ""));
            Require(AutoUpdateLogic.ErrorInUpdate(record, root, "Packages\\com.studioraming.helper\\Editor\\B.cs"));   // the compiler's Windows separators
            // Git: the package lives in Library/PackageCache under a new hash; the compiler still names it Packages/<name>/.
            var git = new SelfUpdateRecord { Route = "git", PackageName = "com.studioraming.manager" };
            Require(AutoUpdateLogic.ErrorInUpdate(git, root, "Packages/com.studioraming.manager/Editor/UI/ManagerWindow.cs"));
            Require(!AutoUpdateLogic.ErrorInUpdate(git, root, "Packages/com.unity.textmeshpro/Scripts/X.cs"));
            Require(!AutoUpdateLogic.ErrorInUpdate(git, root, "Assets/Editor/Mine.cs"));
            // An update in flight or being undone blocks every other update; done, failed and deferred ones do not.
            Require(AutoUpdateLogic.InFlight(new SelfUpdateRecord { Phase = SelfUpdateRecord.PhaseApplying }));
            Require(AutoUpdateLogic.InFlight(new SelfUpdateRecord { Phase = SelfUpdateRecord.PhaseRestoring }));
            Require(!AutoUpdateLogic.InFlight(new SelfUpdateRecord { Phase = SelfUpdateRecord.PhaseFailed }));
            Require(!AutoUpdateLogic.InFlight(null));
        };
    }

    private static Action Record(string parent)
    {
        return () =>
        {
            var root = Path.Combine(parent, "autoupdate-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            Require(AutoUpdateLogic.Load(root) == null);
            var record = new SelfUpdateRecord
            {
                From = "0.1.4", To = "0.1.5", Route = "embedded", Phase = SelfUpdateRecord.PhaseApplying,
                PackageFolder = Path.Combine(root, "Packages", "com.studioraming.manager"), Backup = Path.Combine(root, "Library", "OnAirDeck", "Backup", "b"),
                ManifestPath = Path.Combine(root, "Packages", "vpm-manifest.json"), ManifestBackup = Path.Combine(root, "Library", "m.json"),
                OldPackageId = "com.studioraming.manager@https://example.invalid/m.git#0.1.4", Error = "", FirstRun = true, Manual = true,
            };
            record.PackageName = "com.studioraming.manager";
            record.Swaps.Add(new SelfUpdateSwap { Name = "n1", Target = "t1", Backup = "b1", Restored = true });
            record.Swaps.Add(new SelfUpdateSwap { Name = "n2", Target = "t2", Backup = "" });
            AutoUpdateLogic.Save(root, record);
            var back = AutoUpdateLogic.Load(root);
            Equal(record.From, back.From); Equal(record.To, back.To); Equal(record.Route, back.Route); Equal(record.Phase, back.Phase);
            Equal(record.PackageFolder, back.PackageFolder); Equal(record.Backup, back.Backup); Equal(record.OldPackageId, back.OldPackageId);
            Equal(record.ManifestPath, back.ManifestPath); Equal(record.ManifestBackup, back.ManifestBackup);
            Require(back.FirstRun && back.Manual);
            Equal("n1|t1|b1|True,n2|t2||False", string.Join(",", back.Swaps.Select(s => s.Name + "|" + s.Target + "|" + s.Backup + "|" + s.Restored)));
            Equal("com.studioraming.manager", back.PackageName);
            Require(back.Updated.Length > 0);
            record.FirstRun = false;
            AutoUpdateLogic.Save(root, record);
            Require(!AutoUpdateLogic.Load(root).FirstRun);
            File.WriteAllText(AutoUpdateLogic.RecordPath(root), "not json");
            Require(AutoUpdateLogic.Load(root) == null);
        };
    }

    private static Action Rollback(string parent)
    {
        return () =>
        {
            var root = Path.Combine(parent, "autoupdate-rb-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var folder = Path.Combine(root, "Packages", "com.studioraming.manager");
            var backup = Path.Combine(root, "Library", "OnAirDeck", "Backup", "com.studioraming.manager-0.1.4-20261031");
            var helper = Path.Combine(root, "Packages", "com.studioraming.helper");   // a dependency the new release brought
            Directory.CreateDirectory(Path.Combine(folder, "Editor"));
            File.WriteAllText(Path.Combine(folder, "package.json"), "{\"version\":\"0.1.5\"}");
            File.WriteAllText(Path.Combine(folder, "Editor", "New.cs"), "broken");
            Directory.CreateDirectory(Path.Combine(backup, "Editor"));
            File.WriteAllText(Path.Combine(backup, "package.json"), "{\"version\":\"0.1.4\"}");
            File.WriteAllText(Path.Combine(backup, "Editor", "Old.cs"), "works");
            Directory.CreateDirectory(helper);
            File.WriteAllText(Path.Combine(helper, "package.json"), "{}");
            var manifest = Path.Combine(root, "Packages", "vpm-manifest.json");
            var manifestBackup = Path.Combine(root, "Library", "OnAirDeck", "Backup", "vpm-manifest.json");
            File.WriteAllText(manifest, "{\"new\":1}");
            File.WriteAllText(manifestBackup, "{\"old\":1}");

            // Refusals first: nothing moves.
            var failedRoot = Path.Combine(root, "Library", "OnAirDeck", "Backup");
            try { AutoUpdateLogic.RestoreBackup(folder, Path.Combine(root, "missing"), failedRoot); throw new Exception("a missing backup did not refuse"); }
            catch (PackageInstallException) { }
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            try { AutoUpdateLogic.RestoreBackup(folder, empty, failedRoot); throw new Exception("an empty backup did not refuse"); }
            catch (PackageInstallException) { }
            Require(File.ReadAllText(Path.Combine(folder, "package.json")).Contains("0.1.5"));

            var record = new SelfUpdateRecord { PackageFolder = folder, Backup = backup, ManifestPath = manifest, ManifestBackup = manifestBackup };
            // With nothing recorded there is nothing to put back: an untouched Manager is never emptied.
            try { AutoUpdateLogic.RestoreAll(record, failedRoot, null); throw new Exception("an empty record did not refuse"); }
            catch (PackageInstallException) { }
            Require(File.Exists(Path.Combine(folder, "Editor", "New.cs")));
            record.Swaps.Add(new SelfUpdateSwap { Name = "com.studioraming.manager", Target = folder, Backup = backup });
            record.Swaps.Add(new SelfUpdateSwap { Name = "com.studioraming.helper", Target = helper, Backup = "" });
            var saves = 0;
            AutoUpdateLogic.RestoreAll(record, failedRoot, () => saves++);
            Equal(4, saves);   // two per folder: once the new files left it (FailedDir), once it is back (Restored)
            Require(record.Swaps.All(s => s.Restored));
            // Resumed after a cut: every folder already back is skipped (its backup is empty now and would refuse).
            AutoUpdateLogic.RestoreAll(record, failedRoot, () => saves++);
            Equal(4, saves);
            Require(File.ReadAllText(Path.Combine(folder, "package.json")).Contains("0.1.4"));
            Require(File.Exists(Path.Combine(folder, "Editor", "Old.cs")) && !File.Exists(Path.Combine(folder, "Editor", "New.cs")));
            Require(File.Exists(Path.Combine(backup + "-failed", "Editor", "New.cs")));
            Equal(0, Directory.GetFileSystemEntries(backup).Length);
            Require(!Directory.Exists(helper));   // created by the update: gone from Packages/, so Unity no longer finds it
            Require(File.Exists(Path.Combine(failedRoot, "com.studioraming.helper-new-failed", "package.json")));
            Equal("{\"old\":1}", File.ReadAllText(manifest));
        };
    }

    private static Action RollbackUndo(string parent)
    {
        return () =>
        {
            var root = Path.Combine(parent, "autoupdate-undo-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var folder = Path.Combine(root, "Packages", "com.studioraming.manager");
            var backup = Path.Combine(root, "Library", "OnAirDeck", "Backup", "old");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a-new.txt"), "new");
            File.WriteAllText(Path.Combine(folder, "b-new.txt"), "new");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "a-old.txt"), "old");
            File.WriteAllText(Path.Combine(backup, "z-old.txt"), "old");
            // A file of the backup is held open: the move of it fails after the new files went out.
            using (new FileStream(Path.Combine(backup, "z-old.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { AutoUpdateLogic.RestoreBackup(folder, backup, Path.Combine(root, "Library")); throw new Exception("a held file did not stop the restore"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Equal("a-new.txt,b-new.txt", string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n)));
            Equal("a-old.txt,z-old.txt", string.Join(",", Directory.GetFiles(backup).Select(Path.GetFileName).OrderBy(n => n)));
        };
    }

    private static Action ResumeFromDisk(string parent)
    {
        return () =>
        {
            var root = Path.Combine(parent, "autoupdate-resume-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var folder = Path.Combine(root, "Packages", "com.studioraming.manager");
            var backup = Path.Combine(root, "Library", "OnAirDeck", "Backup", "com.studioraming.manager-0.1.4");
            var failed = backup + "-failed";
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(failed);
            // The state the cut left: every new file in "-failed", a-old already back, z-old still in the backup.
            File.WriteAllText(Path.Combine(failed, "new.cs"), "new");
            File.WriteAllText(Path.Combine(folder, "a-old.cs"), "old");
            File.WriteAllText(Path.Combine(backup, "z-old.cs"), "old");
            var record = new SelfUpdateRecord { Route = "embedded", Phase = SelfUpdateRecord.PhaseRestoring, PackageFolder = folder, Backup = backup };
            record.Swaps.Add(new SelfUpdateSwap { Name = "com.studioraming.manager", Target = folder, Backup = backup, FailedDir = failed });
            AutoUpdateLogic.Save(root, record);
            var loaded = AutoUpdateLogic.Load(root);   // as the next editor load reads it
            Equal(failed, loaded.Swaps[0].FailedDir);
            AutoUpdateLogic.RestoreAll(loaded, Path.Combine(root, "Library", "OnAirDeck", "Backup"), () => AutoUpdateLogic.Save(root, loaded));
            Equal("a-old.cs,z-old.cs", string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n)));
            Equal("new.cs", string.Join(",", Directory.GetFiles(failed).Select(Path.GetFileName)));
            Require(AutoUpdateLogic.Load(root).Swaps[0].Restored);
        };
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
