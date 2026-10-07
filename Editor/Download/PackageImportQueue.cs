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
        private static bool _importing;

        static PackageImportQueue()
        {
            EditorApplication.delayCall += ProcessNext;
        }

        public static void Enqueue(IEnumerable<string> packagePaths)
        {
            var list = Load();
            foreach (var path in packagePaths)
            {
                if (!list.Contains(path)) list.Add(path);
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
            AssetDatabase.importPackageCompleted += OnCompleted;
            AssetDatabase.importPackageCancelled += OnCancelled;
            AssetDatabase.importPackageFailed += OnFailed;
            AssetDatabase.ImportPackage(list[0], true);
        }

        private static void OnCompleted(string packageName)
        {
            Finish("Imported " + packageName, false);
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

            var list = Load();
            if (list.Count > 0)
            {
                var done = list[0];
                list.RemoveAt(0);
                Save(list);
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
