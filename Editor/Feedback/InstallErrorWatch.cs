using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
// UnityEditor.Compilation also has an Assembly type; name the reflection one explicitly.
using ReflectedAssembly = System.Reflection.Assembly;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Watches what an import of one product leaves behind: compiler errors in its scripts, console errors that name its
    /// files, and exceptions thrown from its own assemblies. A watch starts when the Manager starts an import (or installs a
    /// package), ends two minutes after the script compile that follows (<see cref="InstallWatchState"/>), and hands what it
    /// finds to <see cref="InstallErrorReporter"/>, which asks the person before anything is sent.
    /// The state lives in SessionState, so a watch survives the domain reload a successful compile causes; a failed compile
    /// does not reload and the same handlers run on. License-gate texts and errors of other packages are ignored.
    /// </summary>
    [InitializeOnLoad]
    internal static class InstallErrorWatch
    {
        private const string StateKey = "OnAirDeck.UnityManager.InstallWatches";
        private const int MaxStackLines = 20;
        private const int MaxConditionChars = 2000;

        [Serializable]
        private sealed class StateFile
        {
            public List<InstallWatchState> Watches = new List<InstallWatchState>();
        }

        private struct LogItem
        {
            public string Condition;
            public string Stack;
            public LogType Type;
        }

        private static readonly object Gate = new object();
        private static readonly List<LogItem> Logs = new List<LogItem>();
        private static List<InstallWatchState> _watches;
        private static bool _ticking;

        static InstallErrorWatch()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
            CompilationPipeline.compilationStarted -= OnCompileStarted;
            CompilationPipeline.compilationStarted += OnCompileStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationFinished -= OnCompileFinished;
            CompilationPipeline.compilationFinished += OnCompileFinished;
            Load();
            // A successful compile ends in a domain reload, so the compile-finished event of the old domain never reached
            // the watches. A watch that saw the compile start and now finds the editor idle lives in the domain it produced.
            var changed = false;
            foreach (var watch in _watches)
            {
                if (watch.CompileStarted && !watch.CompileDone && !EditorApplication.isCompiling)
                {
                    watch.CompileFinished(DateTime.UtcNow.Ticks);
                    changed = true;
                }
            }
            if (changed) Save();
            UpdateTicking();
        }

        /// <summary>Starts watching an import. <paramref name="assetPaths"/> are the project paths the import brings.</summary>
        public static void Begin(string product, string productId, string version, IEnumerable<string> assetPaths)
        {
            if (string.IsNullOrEmpty(product)) return;
            var prefixes = InstallErrorLogic.ReducePrefixes(assetPaths);
            if (prefixes.Count == 0) return;
            Load();
            _watches.RemoveAll(w => string.Equals(w.Product, product, StringComparison.OrdinalIgnoreCase) && string.Equals(w.Version, version ?? "", StringComparison.OrdinalIgnoreCase));
            var state = InstallWatchState.Begin(product, productId, version, prefixes, DateTime.UtcNow.Ticks);
            state.Assemblies.AddRange(FindAssemblies(state.Prefixes));
            _watches.Add(state);
            Save();
            UpdateTicking();
        }

        /// <summary>The import of the product finished (its scripts, if any, compile next).</summary>
        public static void ImportFinished(string product, string version)
        {
            Load();
            foreach (var watch in _watches)
            {
                if (!Matches(watch, product, version)) continue;
                watch.ImportFinished(DateTime.UtcNow.Ticks);
                watch.Assemblies.Clear();
                watch.Assemblies.AddRange(FindAssemblies(watch.Prefixes));
            }
            Save();
        }

        /// <summary>The import was cancelled or failed: nothing was installed, so there is nothing to watch.</summary>
        public static void End(string product, string version)
        {
            Load();
            if (_watches.RemoveAll(w => Matches(w, product, version)) > 0) Save();
            UpdateTicking();
        }

        private static bool Matches(InstallWatchState watch, string product, string version)
        {
            return string.Equals(watch.Product, product ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(watch.Version, version ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------ compile

        private static void OnCompileStarted(object context)
        {
            Load();
            var changed = false;
            foreach (var watch in _watches)
                if (!watch.CompileStarted) { watch.CompileStarted = true; changed = true; }
            if (changed) Save();
        }

        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;
            Load();
            foreach (var watch in _watches)
            {
                InstallErrorReport report = null;
                foreach (var message in messages)
                {
                    if (message.type != CompilerMessageType.Error) continue;
                    if (InstallErrorLogic.IsLicenseGateText(message.message)) continue;
                    if (!InstallErrorLogic.IsUnder(message.file, watch.Prefixes) && !InstallErrorLogic.MentionsPath(message.message, watch.Prefixes)) continue;
                    if (report == null) report = NewReport(watch, InstallErrorCodes.CompileFailed);
                    if (report.Compile.Count < InstallErrorReport.MaxLines) report.Compile.Add(message.message);
                }
                if (report != null) InstallErrorReporter.Offer(report);
            }
        }

        private static void OnCompileFinished(object context)
        {
            Load();
            var now = DateTime.UtcNow.Ticks;
            foreach (var watch in _watches) watch.CompileFinished(now);
            Save();
        }

        // ------------------------------------------------------------ console

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception) return;
            // Runs inside Unity's log callback, on any thread: only remember the line, judge it on the main thread.
            lock (Gate)
            {
                if (!_ticking) return;
                Logs.Add(new LogItem
                {
                    Condition = Trim(condition, MaxConditionChars),
                    Stack = CompactStack(stackTrace),
                    Type = type,
                });
                if (Logs.Count > 200) Logs.RemoveAt(0);
            }
        }

        private static void Tick()
        {
            LogItem[] items;
            lock (Gate)
            {
                items = Logs.ToArray();
                Logs.Clear();
            }
            foreach (var item in items) Judge(item);

            var now = DateTime.UtcNow.Ticks;
            if (_watches.RemoveAll(w => !w.IsActive(now)) > 0) Save();
            UpdateTicking();
        }

        private static void Judge(LogItem item)
        {
            if (InstallErrorLogic.IsLicenseGateText(item.Condition) || InstallErrorLogic.IsLicenseGateText(item.Stack)) return;
            // Compiler lines come through the compile pipeline with their file, once.
            if (InstallErrorLogic.IsCompilerLine(item.Condition)) return;
            foreach (var watch in _watches)
            {
                var named = InstallErrorLogic.MentionsPath(item.Condition, watch.Prefixes);
                var attributed = item.Type == LogType.Exception
                    ? named || InstallErrorLogic.MentionsPath(item.Stack, watch.Prefixes) || StackInvolves(item.Stack, watch.Assemblies)
                    : named;
                if (!attributed) continue;
                var report = NewReport(watch, InstallErrorCodes.Exception);
                report.Other.Add(item.Type + ": " + item.Condition.TrimEnd() + (item.Stack.Length > 0 ? "\n" + item.Stack : ""));
                InstallErrorReporter.Offer(report);
            }
        }

        private static InstallErrorReport NewReport(InstallWatchState watch, string code)
        {
            return new InstallErrorReport { Product = watch.Product, ProductId = watch.ProductId, Version = watch.Version, BaseCode = code };
        }

        // ------------------------------------------------------------ attribution by assembly

        // Frames look like "Namespace.Type:Method (args) (at Assets/File.cs:12)" in the Console and
        // "  at Namespace.Type.Method (args)" in an exception's own text.
        private static bool StackInvolves(string stack, List<string> assemblyNames)
        {
            if (string.IsNullOrEmpty(stack) || assemblyNames.Count == 0) return false;
            ReflectedAssembly[] loaded = null;
            foreach (var raw in stack.Split('\n'))
            {
                var typeName = TypeNameOf(raw);
                if (typeName.Length == 0) continue;
                if (loaded == null) loaded = WatchedAssemblies(assemblyNames);
                foreach (var assembly in loaded)
                {
                    try { if (assembly.GetType(typeName, false) != null) return true; }
                    catch (Exception) { /* a broken assembly cannot vouch for the frame */ }
                }
            }
            return false;
        }

        internal static string TypeNameOf(string frame)
        {
            var line = frame.Trim();
            if (line.StartsWith("at ", StringComparison.Ordinal)) line = line.Substring(3);
            var paren = line.IndexOf(" (", StringComparison.Ordinal);
            if (paren < 0) paren = line.IndexOf('(');
            if (paren <= 0) return "";
            var head = line.Substring(0, paren);
            var colon = head.IndexOf(':');
            if (colon > 0) return head.Substring(0, colon);
            var dot = head.LastIndexOf('.');
            return dot > 0 ? head.Substring(0, dot) : "";
        }

        private static ReflectedAssembly[] WatchedAssemblies(List<string> names)
        {
            var found = new List<ReflectedAssembly>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = assembly.GetName().Name; }
                catch (Exception) { continue; }
                if (names.Contains(name)) found.Add(assembly);
            }
            return found.ToArray();
        }

        /// <summary>Assembly names the imported paths define: asmdef files (their "name") and managed DLLs (their file name).</summary>
        private static List<string> FindAssemblies(List<string> prefixes)
        {
            var names = new List<string>();
            var root = PackageInstallService.ProjectRoot;
            foreach (var prefix in prefixes)
            {
                try
                {
                    var full = Path.Combine(root, prefix);
                    var files = new List<string>();
                    if (Directory.Exists(full))
                    {
                        files.AddRange(Directory.GetFiles(full, "*.asmdef", SearchOption.AllDirectories));
                        files.AddRange(Directory.GetFiles(full, "*.dll", SearchOption.AllDirectories));
                    }
                    else if (File.Exists(full)) files.Add(full);
                    foreach (var file in files)
                    {
                        string name = null;
                        if (file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                        {
                            var match = Regex.Match(File.ReadAllText(file), "\"name\"\\s*:\\s*\"([^\"]+)\"");
                            if (match.Success) name = match.Groups[1].Value;
                        }
                        else if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(file);
                        if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not list the assemblies of " + prefix + ": " + e.Message);
                }
            }
            return names;
        }

        // ------------------------------------------------------------ state

        private static void UpdateTicking()
        {
            EditorApplication.update -= Tick;
            lock (Gate)
            {
                _ticking = _watches.Count > 0;
                if (!_ticking) Logs.Clear();
            }
            if (_ticking) EditorApplication.update += Tick;
        }

        private static void Load()
        {
            if (_watches != null) return;
            _watches = new List<InstallWatchState>();
            var raw = SessionState.GetString(StateKey, "");
            if (raw.Length == 0) return;
            try
            {
                var file = JsonUtility.FromJson<StateFile>(raw);
                if (file != null && file.Watches != null) _watches = file.Watches;
            }
            catch (Exception)
            {
                // An unreadable state is the same as no watch.
            }
        }

        private static void Save()
        {
            SessionState.SetString(StateKey, JsonUtility.ToJson(new StateFile { Watches = _watches }));
        }

        private static string Trim(string text, int max)
        {
            text = text ?? "";
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private static string CompactStack(string stack)
        {
            return string.IsNullOrEmpty(stack) ? "" : ConsoleErrorBuffer.FirstLines(stack, MaxStackLines);
        }
    }
}
