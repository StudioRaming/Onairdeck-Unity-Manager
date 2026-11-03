using System;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Holds script reloads while the import queue imports one package (G4 design A, 2026-11-04). A package with scripts compiles
    /// during its import; a reload then would drop the queue's completion callbacks (retest D2). Held, the reload waits until the
    /// import ended, and the callback runs. One hold at a time, released exactly once: when the import ends (its callback, or
    /// <see cref="ImportWatch"/> settling it) or on a throw from the import call. No UnityEditor: the queue passes the lock calls in.
    /// </summary>
    internal sealed class ImportReloadHold
    {
        private readonly Action _lockReloads;
        private readonly Action _unlockReloads;

        public ImportReloadHold(Action lockReloads, Action unlockReloads)
        {
            _lockReloads = lockReloads;
            _unlockReloads = unlockReloads;
        }

        public bool Held { get; private set; }

        public void Hold()
        {
            if (Held) return;
            _lockReloads();
            Held = true;
        }

        /// <summary>False when nothing was held (a late or second callback), so it never unlocks another holder's lock.</summary>
        public bool Release()
        {
            if (!Held) return false;
            Held = false;
            _unlockReloads();
            return true;
        }

        /// <summary>Holds, then runs the import call. A throw releases at once and is handed back (null when the call returned).</summary>
        public Exception Run(Action import)
        {
            Hold();
            try
            {
                import();
                return null;
            }
            catch (Exception e)
            {
                Release();
                return e;
            }
        }
    }

    internal enum ImportWatchStep
    {
        Wait,
        Settle
    }

    /// <summary>
    /// When the queue stops waiting for an import's callback and settles it from disk (design A review r1). Unity 2021.3 raises no
    /// callback when its import window is closed with X (PackageImport has no close hook), and none when the window never opens, so a
    /// held import must end without one (R1); but never while the window is open, where the person's Import or Cancel is still to
    /// come and a reload would drop it (R2).
    /// </summary>
    internal static class ImportWatch
    {
        /// <summary>How long an interactive import's window must be gone (and Unity idle) before it settles.</summary>
        public const double ClosedGraceSeconds = 5;

        /// <summary>
        /// How long an import may go without its callback when there is no closed window to go by: a non-interactive import (its
        /// callback normally comes within the import call) or a window never shown (Unity may still be unpacking a large package, or
        /// refused the package with its own dialog).
        /// </summary>
        public const double NoCallbackSeconds = 600;

        /// <param name="interactive">The import shows Unity's import window.</param>
        /// <param name="windowOpen">An import window is open now.</param>
        /// <param name="windowSeen">An import window was seen open since the import started.</param>
        /// <param name="editorBusy">Unity is compiling or updating assets: a callback may still come.</param>
        /// <param name="sinceStart">Seconds since the import started (or was picked up again after a reload).</param>
        /// <param name="sinceWindow">Seconds since an import window was last seen open.</param>
        public static ImportWatchStep Next(bool interactive, bool windowOpen, bool windowSeen, bool editorBusy, double sinceStart, double sinceWindow)
        {
            if (windowOpen || editorBusy) return ImportWatchStep.Wait;
            if (interactive && windowSeen) return sinceWindow >= ClosedGraceSeconds ? ImportWatchStep.Settle : ImportWatchStep.Wait;
            return sinceStart >= NoCallbackSeconds ? ImportWatchStep.Settle : ImportWatchStep.Wait;
        }
    }
}
