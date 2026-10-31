using System;
using System.IO;
using System.Linq;
using OnAirDeck.UnityManager;

// The automatic self-update (owner decision 2026-10-31): when to ask, when to install, the note after the reload, and the rollback
// of an embedded update whose code does not compile.
internal static class AutoUpdateChecks
{
    public static void Run(Action<string, Action> check, string project)
    {
        check("auto update timing: asked on open unless asked within 10 minutes, once a day while open, at once when never asked or the clock went back", Timing);
        check("auto update decision: installs only a newer release this install can take, never one that failed here, and waits while an install, import, compile or play mode runs", Decision);
        check("auto update note: 'updated' once in the new version, 'rolled-back' in the old one, nothing while applying or after it was seen", Note);
        check("auto update record: written and read back with every field; a missing or broken file reads as none", Record(project));
        check("auto update rollback: the old contents come back from the backup and the failed ones are kept beside it; a missing or empty backup refuses before anything moves", Rollback(project));
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
        Equal(AutoUpdateAction.Apply, AutoUpdateLogic.Decide(true, true, "0.1.5", "", safe));
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(false, true, "0.1.5", "", safe));
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(true, false, "0.1.5", "", safe));   // git working copy, local disk, no URL
        Equal(AutoUpdateAction.None, AutoUpdateLogic.Decide(true, true, "", "", safe));
        Equal(AutoUpdateAction.SkipFailed, AutoUpdateLogic.Decide(true, true, "0.1.5", "0.1.5", safe));
        Equal(AutoUpdateAction.Apply, AutoUpdateLogic.Decide(true, true, "0.1.6", "0.1.5", safe));   // a later release is tried again
        foreach (var busy in new[]
        {
            new AutoUpdateMoment { WindowBusy = true }, new AutoUpdateMoment { Compiling = true }, new AutoUpdateMoment { Playing = true },
            new AutoUpdateMoment { ImportsQueued = true }, new AutoUpdateMoment { ImportRequest = true },
        })
            Equal(AutoUpdateAction.Wait, AutoUpdateLogic.Decide(true, true, "0.1.5", "", busy));
    }

    private static void Note()
    {
        var record = new SelfUpdateRecord { From = "0.1.4", To = "0.1.5", Phase = SelfUpdateRecord.PhaseApplied };
        Equal("updated", AutoUpdateLogic.NoteFor(record, "0.1.5"));
        Equal(null, AutoUpdateLogic.NoteFor(record, "0.1.4"));
        record.Phase = SelfUpdateRecord.PhaseRolledBack;
        Equal("rolled-back", AutoUpdateLogic.NoteFor(record, "0.1.4"));
        Equal(null, AutoUpdateLogic.NoteFor(record, "0.1.5"));
        foreach (var phase in new[] { SelfUpdateRecord.PhaseApplying, SelfUpdateRecord.PhaseShown, SelfUpdateRecord.PhaseFailed })
        {
            record.Phase = phase;
            Equal(null, AutoUpdateLogic.NoteFor(record, "0.1.5"));
            Equal(null, AutoUpdateLogic.NoteFor(record, "0.1.4"));
        }
        Equal(null, AutoUpdateLogic.NoteFor(null, "0.1.4"));
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
                OldPackageId = "com.studioraming.manager@https://example.invalid/m.git#0.1.4", Error = "",
            };
            AutoUpdateLogic.Save(root, record);
            var back = AutoUpdateLogic.Load(root);
            Equal(record.From, back.From); Equal(record.To, back.To); Equal(record.Route, back.Route); Equal(record.Phase, back.Phase);
            Equal(record.PackageFolder, back.PackageFolder); Equal(record.Backup, back.Backup); Equal(record.OldPackageId, back.OldPackageId);
            Require(back.Updated.Length > 0);
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
            Directory.CreateDirectory(Path.Combine(folder, "Editor"));
            File.WriteAllText(Path.Combine(folder, "package.json"), "{\"version\":\"0.1.5\"}");
            File.WriteAllText(Path.Combine(folder, "Editor", "New.cs"), "broken");
            Directory.CreateDirectory(Path.Combine(backup, "Editor"));
            File.WriteAllText(Path.Combine(backup, "package.json"), "{\"version\":\"0.1.4\"}");
            File.WriteAllText(Path.Combine(backup, "Editor", "Old.cs"), "works");

            // Refusals first: nothing moves.
            try { AutoUpdateLogic.RestoreBackup(folder, Path.Combine(root, "missing")); throw new Exception("a missing backup did not refuse"); }
            catch (PackageInstallException) { }
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            try { AutoUpdateLogic.RestoreBackup(folder, empty); throw new Exception("an empty backup did not refuse"); }
            catch (PackageInstallException) { }
            Require(File.ReadAllText(Path.Combine(folder, "package.json")).Contains("0.1.5"));

            AutoUpdateLogic.RestoreBackup(folder, backup);
            Require(File.ReadAllText(Path.Combine(folder, "package.json")).Contains("0.1.4"));
            Require(File.Exists(Path.Combine(folder, "Editor", "Old.cs")) && !File.Exists(Path.Combine(folder, "Editor", "New.cs")));
            var failed = backup + "-failed";
            Require(File.Exists(Path.Combine(failed, "Editor", "New.cs")));
            Equal(0, Directory.GetFileSystemEntries(backup).Length);
        };
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
