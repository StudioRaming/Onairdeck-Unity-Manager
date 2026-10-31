using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Imports downloaded .unitypackage files one at a time with Unity's normal import dialog.
    /// The queue lives in SessionState, so if a package contains scripts and the import triggers
    /// a domain reload, the remaining packages still get imported afterwards.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageImportQueue
    {
        private const string Key = "OnAirDeck.UnityManager.PendingImports";
        private const string InteractiveKey = "OnAirDeck.UnityManager.InteractiveImports";
        // One "path<TAB>product<TAB>version<TAB>productId" line per queued package that names its product (see InstallErrorWatch).
        private const string ContextKey = "OnAirDeck.UnityManager.PendingImportContexts";
        private const string RecordsKey = "OnAirDeck.UnityManager.PendingImportRecords";
        // "path<TAB>result" lines of the last finished imports (newest last), for a caller that waits on one (the one-button import).
        private const string ResultsKey = "OnAirDeck.UnityManager.ImportResults";
        private const int MaxResults = 50;
        public const string ResultPending = "pending";
        public const string ResultImported = "imported";
        public const string ResultCancelled = "cancelled";
        public const string ResultFailed = "failed";
        public const string ResultMissing = "missing";
        private static bool _importing;

        /// <summary>Raised after an install record was saved (the window re-reads which versions are installed).</summary>
        public static event Action RecordSaved;
        /// <summary>
        /// Raised with the package files just queued, before the first of them starts importing (an import with scripts may reload the
        /// domain before the code that queued them runs on). A failing listener does not stop the queue.
        /// </summary>
        public static event Action<IList<string>> Queued;
        // The package's files that existed before its import, with their write times (see ReimportUnchanged).
        private static Dictionary<string, DateTime> _overwritten;

        static PackageImportQueue()
        {
            EditorApplication.delayCall += ProcessNext;
        }

        /// <summary>Queues the packages; <paramref name="record"/>, when given, is saved after all of them imported (none if one fails).</summary>
        public static void Enqueue(IEnumerable<string> packagePaths, bool interactive = true, InstallRecord record = null,
            string product = null, string version = null, string productId = null)
        {
            var list = Load();
            if (list.Count > 0 && SessionState.GetBool(InteractiveKey, true) != interactive)
                throw new InvalidOperationException("Cannot change import interaction while packages are queued.");
            var added = new List<string>();
            foreach (var path in packagePaths)
            {
                if (!list.Contains(path)) list.Add(path);
                added.Add(path);
            }
            if (record != null)
                SessionState.SetString(RecordsKey, PendingImportRecords.Add(SessionState.GetString(RecordsKey, ""), added, Guid.NewGuid().ToString("N"), record));
            SessionState.SetBool(InteractiveKey, interactive);
            if (!string.IsNullOrEmpty(product))
            {
                var contexts = LoadContexts();
                foreach (var path in packagePaths) contexts[path] = new[] { product, version ?? "", productId ?? "" };
                SaveContexts(contexts);
            }
            Save(list);
            try
            {
                var queued = Queued;
                if (queued != null) queued(added);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "A listener of the import queue failed: " + e.Message);
            }
            ProcessNext();
        }

        /// <summary>
        /// Saves an install record now. Never throws (it runs inside the queue's completion step, which must go on to the next
        /// package): a failed write or a failing listener is logged; the install itself already succeeded.
        /// </summary>
        public static void SaveRecord(InstallRecord record)
        {
            try
            {
                InstallRecords.Save(PackageInstallService.ProjectRoot, record);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Installed, but the installed version could not be recorded: " + e.Message);
                return;
            }
            try
            {
                var handler = RecordSaved;
                if (handler != null) handler();
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "The installed version was recorded, but the window could not refresh: " + e.Message);
            }
        }

        private static void FinishRecord(string packagePath, bool imported)
        {
            try
            {
                InstallRecord ready;
                SessionState.SetString(RecordsKey, PendingImportRecords.Finish(SessionState.GetString(RecordsKey, ""), packagePath, imported, out ready));
                if (ready != null) SaveRecord(ready);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "The installed version could not be recorded: " + e.Message);
            }
        }

        public static int PendingCount
        {
            get { return Load().Count; }
        }

        /// <summary>
        /// How the import of <paramref name="packagePath"/> ended: <see cref="ResultPending"/> while it is queued, imported, cancelled,
        /// failed or missing (the file was gone), or "" when this editor session knows nothing of it.
        /// </summary>
        public static string ResultOf(string packagePath)
        {
            if (Load().Contains(packagePath)) return ResultPending;
            var result = "";
            foreach (var line in SessionState.GetString(ResultsKey, "").Split('\n'))
            {
                var tab = line.IndexOf('\t');
                if (tab > 0 && line.Substring(0, tab) == packagePath) result = line.Substring(tab + 1);
            }
            return result;
        }

        private static void RememberResult(string packagePath, string result)
        {
            var lines = new List<string>();
            foreach (var line in SessionState.GetString(ResultsKey, "").Split('\n'))
                if (line.Length > 0 && !line.StartsWith(packagePath + "\t", StringComparison.Ordinal)) lines.Add(line);
            lines.Add(packagePath + "\t" + result);
            if (lines.Count > MaxResults) lines.RemoveRange(0, lines.Count - MaxResults);
            SessionState.SetString(ResultsKey, string.Join("\n", lines.ToArray()));
        }

        private static void ProcessNext()
        {
            if (_importing) return;
            var list = Load();
            while (list.Count > 0 && !File.Exists(list[0]))
            {
                FinishRecord(list[0], false);
                RememberResult(list[0], ResultMissing);
                list.RemoveAt(0);
            }
            Save(list);
            if (list.Count == 0) return;

            _importing = true;
            var assets = AssetsOf(list[0]);
            _overwritten = ExistingFiles(assets);
            string[] context;
            if (assets != null && LoadContexts().TryGetValue(list[0], out context))
            {
                // Taken before the import: a folder that exists now is not the product's, one the import creates is. The job is
                // the package file, which the queue keeps across a domain reload.
                var root = PackageInstallService.ProjectRoot;
                var owned = InstallErrorLogic.OwnedPaths(assets, p => Directory.Exists(Path.Combine(root, p)) || File.Exists(Path.Combine(root, p)));
                InstallErrorWatch.Begin(list[0], context[0], context[2], context[1], owned);
            }
            AssetDatabase.importPackageCompleted += OnCompleted;
            AssetDatabase.importPackageCancelled += OnCancelled;
            AssetDatabase.importPackageFailed += OnFailed;
            AssetDatabase.ImportPackage(list[0], SessionState.GetBool(InteractiveKey, true) && !Application.isBatchMode);
        }

        private static void OnCompleted(string packageName)
        {
            string[] context;
            if (HeadContext(out context)) InstallErrorWatch.ImportFinished(Load()[0], context[0], context[1]);
            ReimportUnchanged();
            Finish("Imported " + packageName, false, ResultImported);
        }

        private static List<PackageAsset> AssetsOf(string packagePath)
        {
            try
            {
                return UnityPackageArchive.Assets(packagePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not list " + Path.GetFileName(packagePath) + ": " + e.Message);
                return null;
            }
        }

        private static Dictionary<string, DateTime> ExistingFiles(List<PackageAsset> assets)
        {
            if (assets == null) return null;
            var paths = new List<string>();
            foreach (var asset in assets) if (!asset.IsFolder) paths.Add(asset.Path);
            return UnityPackageArchive.ExistingFileTimes(PackageInstallService.ProjectRoot, paths);
        }

        /// <summary>
        /// A .unitypackage carries its file times and Unity keeps them; an overwritten file whose time did not change is not
        /// re-imported, so an update packed with the same fixed time as the installed version left the old DLL loaded
        /// (MaskMaker DRM QA, Unity 2022.3, 2026-10-29). Re-imports the package's files that existed before and kept their time.
        /// </summary>
        private static void ReimportUnchanged()
        {
            var before = _overwritten;
            _overwritten = null;
            if (before == null || before.Count == 0) return;
            var stale = UnityPackageArchive.UnchangedSince(PackageInstallService.ProjectRoot, before);
            if (stale.Count == 0) return;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in stale) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            Debug.Log(OnAirDeckConfig.LogPrefix + "Re-imported " + stale.Count + " file(s) the package overwrote with an unchanged time.");
        }

        private static void OnCancelled(string packageName)
        {
            string[] context;
            if (HeadContext(out context)) InstallErrorWatch.End(Load()[0], context[0], context[1]);
            Finish("Import of " + packageName + " was cancelled", true, ResultCancelled);
        }

        private static void OnFailed(string packageName, string error)
        {
            string[] context;
            if (HeadContext(out context))
            {
                InstallErrorWatch.End(Load()[0], context[0], context[1]);
                var report = new InstallErrorReport { Product = context[0], ProductId = context[2], Version = context[1], Reason = "Import of " + packageName + " failed: " + error };
                report.AddOther(report.Reason);
                InstallErrorReporter.Offer(report);
            }
            Finish("Import of " + packageName + " failed: " + error, true, ResultFailed);
        }

        private static void Finish(string message, bool warn, string result)
        {
            AssetDatabase.importPackageCompleted -= OnCompleted;
            AssetDatabase.importPackageCancelled -= OnCancelled;
            AssetDatabase.importPackageFailed -= OnFailed;
            _overwritten = null;

            var list = Load();
            if (list.Count > 0)
            {
                var done = list[0];
                list.RemoveAt(0);
                Save(list);
                var contexts = LoadContexts();
                if (contexts.Remove(done)) SaveContexts(contexts);
                // warn is a cancelled or failed import: the version did not go in, so its record is dropped.
                FinishRecord(done, !warn);
                RememberResult(done, result);
                // Only our own cache files are deleted; never anything else.
                if (PathSafety.IsInside(Downloader.CacheRoot, done)) Downloader.TryDeleteFolder(Path.GetDirectoryName(done));
            }
            _importing = false;

            if (warn) Debug.LogWarning(OnAirDeckConfig.LogPrefix + message);
            else Debug.Log(OnAirDeckConfig.LogPrefix + message);

            EditorApplication.delayCall += ProcessNext;
        }

        private static bool HeadContext(out string[] context)
        {
            context = null;
            var list = Load();
            return list.Count > 0 && LoadContexts().TryGetValue(list[0], out context);
        }

        private static Dictionary<string, string[]> LoadContexts()
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var line in SessionState.GetString(ContextKey, "").Split('\n'))
            {
                var parts = line.Split('\t');
                if (parts.Length == 4) result[parts[0]] = new[] { parts[1], parts[2], parts[3] };
            }
            return result;
        }

        private static void SaveContexts(Dictionary<string, string[]> contexts)
        {
            var lines = new List<string>();
            foreach (var pair in contexts) lines.Add(pair.Key + "\t" + pair.Value[0] + "\t" + pair.Value[1] + "\t" + pair.Value[2]);
            SessionState.SetString(ContextKey, string.Join("\n", lines.ToArray()));
        }

        private static List<string> Load()
        {
            var raw = SessionState.GetString(Key, "");
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var line in raw.Split('\n'))
            {
                if (line.Length > 0) list.Add(line);
            }
            return list;
        }

        private static void Save(List<string> list)
        {
            SessionState.SetString(Key, string.Join("\n", list.ToArray()));
        }
    }
}
