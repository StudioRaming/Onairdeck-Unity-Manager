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
        private const string RecordsKey = "OnAirDeck.UnityManager.PendingImportRecords";
        private static bool _importing;

        /// <summary>Raised after an install record was saved (the window re-reads which versions are installed).</summary>
        public static event Action RecordSaved;
        // The package's files that existed before its import, with their write times (see ReimportUnchanged).
        private static Dictionary<string, DateTime> _overwritten;

        static PackageImportQueue()
        {
            EditorApplication.delayCall += ProcessNext;
        }

        /// <summary>Queues the packages; <paramref name="record"/>, when given, is saved after all of them imported (none if one fails).</summary>
        public static void Enqueue(IEnumerable<string> packagePaths, bool interactive = true, InstallRecord record = null)
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
            Save(list);
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

        private static void ProcessNext()
        {
            if (_importing) return;
            var list = Load();
            while (list.Count > 0 && !File.Exists(list[0]))
            {
                FinishRecord(list[0], false);
                list.RemoveAt(0);
            }
            Save(list);
            if (list.Count == 0) return;

            _importing = true;
            _overwritten = ExistingFiles(list[0]);
            AssetDatabase.importPackageCompleted += OnCompleted;
            AssetDatabase.importPackageCancelled += OnCancelled;
            AssetDatabase.importPackageFailed += OnFailed;
            AssetDatabase.ImportPackage(list[0], SessionState.GetBool(InteractiveKey, true) && !Application.isBatchMode);
        }

        private static void OnCompleted(string packageName)
        {
            ReimportUnchanged();
            Finish("Imported " + packageName, false);
        }

        private static Dictionary<string, DateTime> ExistingFiles(string packagePath)
        {
            try
            {
                var paths = UnityPackageArchive.AssetPaths(packagePath);
                return paths == null ? null : UnityPackageArchive.ExistingFileTimes(PackageInstallService.ProjectRoot, paths);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not list " + Path.GetFileName(packagePath) + ": " + e.Message);
                return null;
            }
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
            Finish("Import of " + packageName + " was cancelled", true);
        }

        private static void OnFailed(string packageName, string error)
        {
            Finish("Import of " + packageName + " failed: " + error, true);
        }

        private static void Finish(string message, bool warn)
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
                // warn is a cancelled or failed import: the version did not go in, so its record is dropped.
                FinishRecord(done, !warn);
                // Only our own cache files are deleted; never anything else.
                if (PathSafety.IsInside(Downloader.CacheRoot, done)) Downloader.TryDeleteFolder(Path.GetDirectoryName(done));
            }
            _importing = false;

            if (warn) Debug.LogWarning(OnAirDeckConfig.LogPrefix + message);
            else Debug.Log(OnAirDeckConfig.LogPrefix + message);

            EditorApplication.delayCall += ProcessNext;
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
