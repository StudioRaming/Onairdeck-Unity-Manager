using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OnAirDeck.UnityManager
{
    /// <summary>What the window should show instead of an ordinary error.</summary>
    internal enum NoticeKind { DeviceLimit, UpdateRequired, DeviceReleased }

    internal sealed class DeviceNotice
    {
        public NoticeKind Kind;
        /// <summary>Server message (plain English).</summary>
        public string Message = "";
        /// <summary>Set for DeviceLimit: refused licenses and the devices to choose from.</summary>
        public DeviceLimitException DeviceLimit;
        /// <summary>Set for DeviceReleased: the licenses this PC released.</summary>
        public DeviceReleasedException Released;
        /// <summary>Names of the items refused while this notice was open (downloads, installs, renewals).</summary>
        public List<string> Items = new List<string>();
        public DateTime At;

        internal readonly List<RetryEntry> Entries = new List<RetryEntry>();

        /// <summary>
        /// One action that runs every retry of this notice in the order they were reported (a batch's resume, a renewal,
        /// an install...), or null when nothing can be retried. A new object per read; it is not a stable identity.
        /// </summary>
        public Func<Task<object>> Retry
        {
            get
            {
                if (Entries.Count == 0) return null;
                var all = new List<Func<Task<object>>>();
                foreach (var entry in Entries) all.Add(entry.Run);
                return async () =>
                {
                    object last = null;
                    foreach (var run in all) last = await run();
                    return last;
                };
            }
        }
    }

    internal sealed class RetryEntry
    {
        public int Seq;
        public string Key;
        public Func<Task<object>> Run;
        /// <summary>
        /// The entry belongs to something that waits for the user (a paused batch), not to the kind of refusal: a notice of another kind
        /// carries it over instead of dropping it, so the batch still resumes.
        /// </summary>
        public bool KeepAcrossKinds;
    }

    /// <summary>
    /// The one observable "the server refused this PC" state. Services report into it before rethrowing, so a refusal
    /// reaches the window even from silent renewal. The window subscribes to <see cref="Changed"/>, shows
    /// <see cref="Current"/>, releases a device through OnAirDeckApi.ReleaseDeviceAsync (or, for DeviceReleased, takes the
    /// seat again), then calls <see cref="RetryAsync"/>. Retries never overwrite each other: a later report adds its retry
    /// (a report with the same key replaces only its own earlier one), so a background refresh cannot cut off the
    /// retry a paused batch installed. Raised on the editor main thread (every caller continues on the editor context).
    /// </summary>
    internal static class DeviceLimitNotice
    {
        public static event Action Changed;
        public static DeviceNotice Current { get; private set; }

        private static int _seq;

        /// <summary>A position in the retry order; pass it to <see cref="DropSince"/> later.</summary>
        public static int Mark() { return _seq; }

        /// <summary>
        /// Removes the retries reported after <paramref name="mark"/>. A paused batch calls it before installing its own resume:
        /// the retries the running item's services reported would run that item a second time. Retries reported before the
        /// mark (a background renewal) stay. Raises no event; the caller reports right after.
        /// </summary>
        public static void DropSince(int mark)
        {
            if (Current != null) Current.Entries.RemoveAll(e => e.Seq > mark);
        }

        /// <summary>True when the exception is a device-limit, device-released or update-required refusal (and was recorded).</summary>
        /// <param name="retryKey">Retries with the same key replace each other (a repeated background renewal keeps one entry); null always adds.</param>
        /// <param name="keepAcrossKinds">The retry resumes a paused batch: it survives a refusal of another kind (see <see cref="RetryEntry.KeepAcrossKinds"/>).</param>
        public static bool Report(Exception error, string itemName, Func<Task<object>> retry, string retryKey = null, bool keepAcrossKinds = false)
        {
            var limit = error as DeviceLimitException;
            var update = error as ManagerUpdateRequiredException;
            var released = error as DeviceReleasedException;
            if (limit == null && update == null && released == null) return false;
            var kind = limit != null ? NoticeKind.DeviceLimit : released != null ? NoticeKind.DeviceReleased : NoticeKind.UpdateRequired;
            // A refusal of another kind replaces the notice (the old one's retries ran into a different wall); the same kind joins it.
            // Only the retries of a paused batch cross over: its resume and cancel must stay reachable whatever the notice says.
            var notice = Current != null && Current.Kind == kind ? Current : new DeviceNotice { Kind = kind };
            if (Current != null && !ReferenceEquals(notice, Current))
            {
                foreach (var entry in Current.Entries)
                    if (entry.KeepAcrossKinds) notice.Entries.Add(entry);
                if (notice.Entries.Count > 0) notice.Items.AddRange(Current.Items);
            }
            notice.Message = error.Message;
            notice.DeviceLimit = limit;
            notice.Released = released;
            notice.At = DateTime.UtcNow;
            if (retry != null)
            {
                RetryEntry existing = null;
                if (retryKey != null) foreach (var entry in notice.Entries) if (entry.Key == retryKey) existing = entry;
                if (existing != null) { existing.Run = retry; existing.Seq = ++_seq; }
                else notice.Entries.Add(new RetryEntry { Seq = ++_seq, Key = retryKey, Run = retry, KeepAcrossKinds = keepAcrossKinds });
            }
            if (!string.IsNullOrEmpty(itemName) && !notice.Items.Contains(itemName)) notice.Items.Add(itemName);
            Current = notice;
            RaiseChanged();
            return true;
        }

        /// <summary>Overload for actions that return nothing useful.</summary>
        public static bool Report(Exception error, string itemName, Func<Task> retry, string retryKey = null, bool keepAcrossKinds = false)
        {
            Func<Task<object>> wrapped = null;
            if (retry != null) wrapped = async () => { await retry(); return null; };
            return Report(error, itemName, wrapped, retryKey, keepAcrossKinds);
        }

        /// <summary>
        /// Removes one retry by reference (a paused batch took its own back after it resumed). The notice goes away when that
        /// was its last retry; retries other reporters added stay.
        /// </summary>
        public static void Discard(Func<Task<object>> retry)
        {
            if (Current == null || retry == null) return;
            var removed = Current.Entries.RemoveAll(e => ReferenceEquals(e.Run, retry)) > 0;
            if (!removed) return;
            if (Current.Entries.Count == 0) Clear();
            else RaiseChanged();
        }

        /// <summary>Forgets the notice (the user dismissed it, or the cause is gone).</summary>
        public static void Clear()
        {
            if (Current == null) return;
            Current = null;
            RaiseChanged();
        }

        /// <summary>
        /// Clears the notice and runs its retries in order. A refusal during a retry is reported again and rethrown, so call
        /// this in a try/catch like the original action. Returns the last retry's result (a summary or report, may be null),
        /// or null when there was nothing to retry. Every retry here runs for a user click, so it may take back a released seat.
        /// </summary>
        public static async Task<object> RetryAsync()
        {
            var retry = Current != null ? Current.Retry : null;
            Clear();
            return retry == null ? null : await retry();
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
