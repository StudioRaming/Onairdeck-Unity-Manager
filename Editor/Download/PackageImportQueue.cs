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
        private static bool _importing;
        // The package's files that existed before its import, with their write times (see ReimportUnchanged).
        private static Dictionary<string, DateTime> _overwritten;

        static PackageImportQueue()
        {
            EditorApplication.delayCall += ProcessNext;
        }

        public static void Enqueue(IEnumerable<string> packagePaths, bool interactive = true, string product = null, string version = null, string productId = null)
        {
            var list = Load();
            if (list.Count > 0 && SessionState.GetBool(InteractiveKey, true) != interactive)
                throw new InvalidOperationException("Cannot change import interaction while packages are queued.");
            foreach (var path in packagePaths)
            {
                if (!list.Contains(path)) list.Add(path);
            }
            SessionState.SetBool(InteractiveKey, interactive);
            if (!string.IsNullOrEmpty(product))
            {
                var contexts = LoadContexts();
                foreach (var path in packagePaths) contexts[path] = new[] { product, version ?? "", productId ?? "" };
                SaveContexts(contexts);
            }
            Save(list);
            ProcessNext();
        }

        public static int PendingCount
        {
            get { return Load().Count; }
        }

        private static void ProcessNext()
        {
            if (_importing) return;
            var list = Load();
            while (list.Count > 0 && !File.Exists(list[0])) list.RemoveAt(0);
            Save(list);
            if (list.Count == 0) return;

            _importing = true;
            var paths = AssetPathsOf(list[0]);
            _overwritten = ExistingFiles(paths);
            string[] context;
            if (paths != null && LoadContexts().TryGetValue(list[0], out context)) InstallErrorWatch.Begin(context[0], context[2], context[1], paths);
            AssetDatabase.importPackageCompleted += OnCompleted;
            AssetDatabase.importPackageCancelled += OnCancelled;
            AssetDatabase.importPackageFailed += OnFailed;
            AssetDatabase.ImportPackage(list[0], SessionState.GetBool(InteractiveKey, true) && !Application.isBatchMode);
        }

        private static void OnCompleted(string packageName)
        {
            string[] context;
            if (HeadContext(out context)) InstallErrorWatch.ImportFinished(context[0], context[1]);
            ReimportUnchanged();
            Finish("Imported " + packageName, false);
        }

        private static List<string> AssetPathsOf(string packagePath)
        {
            try
            {
                return UnityPackageArchive.AssetPaths(packagePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not list " + Path.GetFileName(packagePath) + ": " + e.Message);
                return null;
            }
        }

        private static Dictionary<string, DateTime> ExistingFiles(List<string> paths)
        {
            return paths == null ? null : UnityPackageArchive.ExistingFileTimes(PackageInstallService.ProjectRoot, paths);
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
            if (HeadContext(out context)) InstallErrorWatch.End(context[0], context[1]);
            Finish("Import of " + packageName + " was cancelled", true);
        }

        private static void OnFailed(string packageName, string error)
        {
            string[] context;
            if (HeadContext(out context))
            {
                InstallErrorWatch.End(context[0], context[1]);
                var report = new InstallErrorReport { Product = context[0], ProductId = context[2], Version = context[1], Reason = "Import of " + packageName + " failed: " + error };
                report.Other.Add(report.Reason);
                InstallErrorReporter.Offer(report);
            }
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
                var contexts = LoadContexts();
                if (contexts.Remove(done)) SaveContexts(contexts);
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
