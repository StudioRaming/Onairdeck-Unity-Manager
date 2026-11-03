using System;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Holds script reloads while the import queue imports one package (G4 design A, 2026-11-04). A package with scripts compiles
    /// during its import; a reload then would drop the queue's completion callbacks (retest D2). Held, the reload waits until the
    /// import ended, and the callback always runs. One hold at a time, released exactly once: by the import's end, by a throw from the
    /// import call, or by <see cref="Tick"/> after <see cref="TimeoutSeconds"/> as a backstop for a callback that never comes.
    /// No UnityEditor: the queue passes the lock calls in, headless-checked.
    /// </summary>
    internal sealed class ImportReloadHold
    {
        /// <summary>How long a hold may last before <see cref="Tick"/> lets reloads run again (an import window a person keeps open).</summary>
        public const double TimeoutSeconds = 600;

        private readonly Action _lockReloads;
        private readonly Action _unlockReloads;
        private readonly Func<double> _now;
        private double _since;

        public ImportReloadHold(Action lockReloads, Action unlockReloads, Func<double> now)
        {
            _lockReloads = lockReloads;
            _unlockReloads = unlockReloads;
            _now = now;
        }

        public bool Held { get; private set; }

        public void Hold()
        {
            if (Held) return;
            _lockReloads();
            Held = true;
            _since = _now();
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

        /// <summary>True when the hold just timed out and was released (the caller logs it).</summary>
        public bool Tick()
        {
            if (!Held || _now() - _since < TimeoutSeconds) return false;
            return Release();
        }
    }
}
