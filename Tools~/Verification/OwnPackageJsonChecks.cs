using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.UnityManager;

// An old Assets install of the product that carries its own package.json (owner report 2026-11-07: MingToon 0.1.12-preview.1-dev in
// Assets/StudioRaming/MingToon with a package.json named com.studioraming.mingtoon). It is the product's old install, so the legacy
// rules take it like any other; a package.json that names another package still keeps the folder out of every plan.
internal static partial class LegacyChecks
{
    private const string OwnFolder = "Assets/StudioRaming/MingToon";
    private const string OwnGuid = "6a6a6a6a6a6a6a6a6a6a6a6a6a6a6a6a";

    private static void RunOwnPackageJson(Action<string, Action> check)
    {
        check("v9d: an old Assets install with a package.json naming the package being installed is moved and the install goes through", OwnPackageJsonMoves);
        check("v9d: that install with a changed file is refused as an old install to clean up (names the file), never as a foreign package, and nothing moves", OwnPackageJsonWithEditsBlocks);
        check("v9d: a package.json naming another package keeps the folder out of the plan, at the configured path and elsewhere", ForeignPackageJsonStaysOut);
    }

    private static string TextSha(string text)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }

    // The old install as the owner had it: Release/MingToon_ReleaseManifest.json lists every shipped file and .meta, but not the
    // package.json (nor its .meta), which sits at the top of the folder.
    private static void OwnInstall(string p, string packageName)
    {
        var shipped = new Dictionary<string, string>
        {
            { OwnFolder + "/Runtime/F0.cs", "shipped code" },
            { OwnFolder + "/Runtime/F0.cs.meta", Meta(OwnGuid) },
            { OwnFolder + "/Runtime.meta", Meta("7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c7c") },
            { OwnFolder + "/Release.meta", Meta("7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e7e") }
        };
        var sb = new StringBuilder("{\"product\":\"MingToon\",\"version\":\"0.1.11\",\"files\":[");
        var first = true;
        foreach (var file in shipped)
        {
            Put(p, file.Key, file.Value);
            sb.Append(first ? "" : ",").Append("{\"path\":\"").Append(file.Key).Append("\",\"sha256\":\"").Append(TextSha(file.Value)).Append("\"}");
            first = false;
        }
        sb.Append("]}");
        Put(p, OwnFolder + "/Release/MingToon_ReleaseManifest.json", sb.ToString());
        Put(p, OwnFolder + "/Release/MingToon_ReleaseManifest.json.meta", Meta("7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d7d"));
        Put(p, OwnFolder + ".meta", Meta("7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a"));
        Put(p, OwnFolder + "/package.json", "{\"name\":\"" + packageName + "\",\"version\":\"0.1.12-preview.1-dev\"}");
        Put(p, OwnFolder + "/package.json.meta", Meta("7b7b7b7b7b7b7b7b7b7b7b7b7b7b7b7b"));
    }

    private static void OwnPackageJsonMoves()
    {
        var p = NewProject();
        OwnInstall(p, Pkg);
        var zip = Zip(Pkg, "1.0.0", "", OwnGuid);

        var plan = LegacyInstall.Detect(p, new[] { Probe(Pkg, zip, null) });
        Equal(OwnFolder, string.Join(",", Paths(plan).ToArray()));
        Equal("config", plan.Items[0].Source);
        Require(!plan.Items[0].Blocked);
        Require(plan.Items[0].Handler is ReleaseManifestHandler);

        LegacyPlan shown = null;
        var move = LegacyInstall.Prepare(p, new[] { Probe(Pkg, zip, null) }, true, x => { shown = x; return true; }, Now);
        Require(move != null && shown != null);
        Require(!Directory.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon")));
        Require(File.Exists(Path.Combine(move.BackupFolder, "Assets", "StudioRaming", "MingToon", "package.json")));
        Equal(0, GuidCollisions.Scan(p, GuidCollisions.ReadZipGuids(zip), new string[0]).Count);
        PackageTransaction.Execute(p, new List<InstallItem> { new InstallItem { Name = Pkg, Version = "1.0.0", ZipPath = zip } }, Now);
        Require(File.Exists(Path.Combine(p, "Packages", Pkg, "package.json")));
    }

    private static void OwnPackageJsonWithEditsBlocks()
    {
        var p = NewProject();
        OwnInstall(p, Pkg);
        Put(p, OwnFolder + "/Runtime/F0.cs", "edited by the user");
        var zip = Zip(Pkg, "1.0.0", "", OwnGuid);
        var before = Snapshot(p);

        var plan = LegacyInstall.Detect(p, new[] { Probe(Pkg, zip, null) });
        Equal(OwnFolder, string.Join(",", Paths(plan).ToArray()));
        Require(plan.Items[0].Blocked && !plan.Items[0].HandOff);
        Equal(OwnFolder + "/Runtime/F0.cs", string.Join(",", plan.Items[0].Unmatched.ToArray()));
        var refusal = LegacyInstall.RefusalBeforeConfirm(plan);
        Require(refusal != null);
        Require(refusal.Message.IndexOf("F0.cs", StringComparison.Ordinal) >= 0);
        Throws<PackageInstallException>(() => LegacyInstall.Move(p, plan, Now));
        Equal(before, Snapshot(p));
    }

    private static void ForeignPackageJsonStaysOut()
    {
        // Another vendor's package at the configured path: not the product's old install.
        var p = NewProject();
        OwnInstall(p, "com.vendor.sdk");
        var zip = Zip(Pkg, "1.0.0", "", OwnGuid);
        Equal(0, LegacyInstall.Detect(p, new[] { Probe(Pkg, zip, null) }).Items.Count);

        // The product's package.json, but another package is being installed: still not ours to move.
        p = NewProject();
        Put(p, "Assets/Vendor/Tool/package.json", "{\"name\":\"" + Pkg + "\",\"version\":\"1.0.0\"}");
        Put(p, "Assets/Vendor/Tool/a.txt", "a");
        zip = Zip(Other, "1.0.0", ",\"atelier\":{\"replacesAssetFolders\":[\"Assets/Vendor/Tool\"]}");
        Equal(0, LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) }).Items.Count);
    }
}
