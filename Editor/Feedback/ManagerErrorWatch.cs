using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Opens the error report card for the Manager's own failures, so the person does not have to find the button: an exception
    /// in the Console whose first non-engine frame is the Manager's (OnGUI, an <c>async void</c> step that let it escape, an
    /// editor callback), a faulted task nobody awaited (<see cref="TaskScheduler.UnobservedTaskException"/>), an exception that
    /// ends a background thread (<see cref="AppDomain.UnhandledException"/>), and an unexpected exception a step of the window
    /// caught (<see cref="OfferCaught"/>). A server, network, cancel or sign-in failure is not a bug and keeps its red message
    /// and the button only (<see cref="ManagerErrorLogic.IsUnexpected"/>). Everything goes through
    /// <see cref="InstallErrorReporter.Offer"/>: shown redacted, deduplicated by signature, sent only on the Send click.
    /// </summary>
    [InitializeOnLoad]
    internal static class ManagerErrorWatch
    {
        private const int MaxQueued = 50;
        private const int MaxSeen = 200;

        private static readonly object Gate = new object();
        private static readonly List<InstallErrorReport> Queue = new List<InstallErrorReport>();
        private static readonly List<KeyValuePair<string, string>> Logs = new List<KeyValuePair<string, string>>();
        // Texts already offered in this domain: an exception thrown on every repaint is judged once.
        private static readonly HashSet<string> Seen = new HashSet<string>();
        private static volatile bool _waiting;
        private static bool _draining;

        static ManagerErrorWatch()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;
        }

        /// <summary>
        /// A step of the window caught <paramref name="e"/>: a card when it is a bug of the Manager (not a server, network, cancel or
        /// sign-in failure). <paramref name="step"/> names what failed in English ("Loading the product list"). Main thread.
        /// </summary>
        /// <returns>The card's report, also when it was offered before (the button next to the red message reopens it), or null.</returns>
        public static InstallErrorReport OfferCaught(string step, Exception e)
        {
            if (!ManagerErrorLogic.IsUnexpected(e)) return null;
            var inner = ManagerErrorLogic.Unwrap(e);
            var text = ManagerErrorLogic.Describe(step, inner);
            var reason = (step ?? "") + ": " + inner.Message;
            Enqueue(text, reason);
            Drain();
            return ManagerErrorLogic.NewReport(UpdateService.CurrentVersion ?? "", reason, text);
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;
            // Runs inside Unity's log callback, on any thread: only remember it, judge it on the main thread. Never log from here.
            lock (Gate)
            {
                if (Logs.Count >= MaxQueued) return;
                Logs.Add(new KeyValuePair<string, string>(condition ?? "", stackTrace ?? ""));
            }
            _waiting = true;
        }

        private static void OnUnobservedTask(object sender, UnobservedTaskExceptionEventArgs args)
        {
            // The finalizer thread: no Unity API here.
            if (args == null || args.Exception == null || !ManagerErrorLogic.ThrownByManager(args.Exception)) return;
            var inner = ManagerErrorLogic.Unwrap(args.Exception);
            Enqueue(ManagerErrorLogic.Describe("Unobserved task", inner), inner.Message);
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
        {
            var e = args != null ? args.ExceptionObject as Exception : null;
            if (e == null || !ManagerErrorLogic.ThrownByManager(e)) return;
            Enqueue(ManagerErrorLogic.Describe("Unhandled exception", e), e.Message);
        }

        private static void Enqueue(string text, string reason)
        {
            lock (Gate)
            {
                if (Queue.Count >= MaxQueued || Seen.Contains(text)) return;
                if (Seen.Count >= MaxSeen) Seen.Clear();
                Seen.Add(text);
                // Version is filled on the main thread.
                var report = ManagerErrorLogic.NewReport("", reason, text);
                if (report != null) Queue.Add(report);
            }
            _waiting = true;
        }

        /// <summary>Judges what the hooks collected and offers the cards. Runs on every editor update (cheap when nothing waits); main thread.</summary>
        internal static void Drain()
        {
            if (!_waiting || _draining) return;
            _draining = true;
            try
            {
                KeyValuePair<string, string>[] logs;
                lock (Gate)
                {
                    _waiting = false;
                    logs = Logs.ToArray();
                    Logs.Clear();
                }
                foreach (var log in logs)
                {
                    if (!ManagerErrorLogic.IsManagerStack(log.Value)) continue;
                    var reason = InstallErrorLogic.FirstLine(log.Key);
                    if (reason.IndexOf("ExitGUIException", StringComparison.Ordinal) >= 0) continue;
                    Enqueue(ManagerErrorLogic.DescribeLog(log.Key, log.Value), reason);
                }
                InstallErrorReport[] reports;
                lock (Gate)
                {
                    _waiting = false;
                    reports = Queue.ToArray();
                    Queue.Clear();
                }
                var version = reports.Length > 0 ? UpdateService.CurrentVersion ?? "" : "";
                foreach (var report in reports)
                {
                    report.Version = version;
                    InstallErrorReporter.Offer(report);
                }
            }
            catch (Exception e)
            {
                // A failure here must not feed itself through the Console hook: say it once as a warning and go on.
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not prepare the error report: " + e.Message);
            }
            finally
            {
                _draining = false;
            }
        }
    }
}
