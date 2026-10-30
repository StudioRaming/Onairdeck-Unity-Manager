using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OnAirDeck.UnityManager
{
    internal enum BatchStatus { Pending, Running, Paused, Done, Failed, Skipped }

    /// <summary>One action of a batch (a download or an install). Run reports (label, 0..1 progress) and returns its result object.</summary>
    internal sealed class BatchItem
    {
        public string Name = "";
        /// <summary>The product version, for the error report when this item fails; "" when unknown.</summary>
        public string Version = "";
        public Func<Action<string, float>, Task<object>> Run;
    }

    internal sealed class BatchResult
    {
        public string Name = "";
        public BatchStatus Status = BatchStatus.Pending;
        /// <summary>Why it failed or was skipped; "" otherwise.</summary>
        public string Message = "";
        public string Version = "";
        /// <summary>The exception that failed it (Failed only), for the error report.</summary>
        public Exception Error;
        /// <summary>What the action returned (a DownloadSummary or InstallReport); null unless Done.</summary>
        public object Result;
    }

    /// <summary>
    /// Runs actions one after another with one progress line. Cancel stops after the current item (the rest are Skipped).
    /// A device-limit or update-required refusal pauses the queue and shows the notice through
    /// <see cref="DeviceLimitNotice"/>; <see cref="DeviceLimitNotice.RetryAsync"/> resumes and retries that item.
    /// A rejected session stops the queue (<see cref="SessionExpired"/>). Any other failure is recorded and the queue goes on.
    /// No UnityEngine here, so the ordering rules are checked headless; see BatchActions for the Unity-side actions.
    /// </summary>
    internal sealed class BatchQueue
    {
        private readonly List<BatchItem> _items;
        private readonly List<BatchResult> _results = new List<BatchResult>();
        private TaskCompletionSource<bool> _resume;
        private bool _started;

        public BatchQueue(IEnumerable<BatchItem> items)
        {
            _items = new List<BatchItem>(items);
            foreach (var item in _items) _results.Add(new BatchResult { Name = item.Name, Version = item.Version });
        }

        public event Action Changed;

        public IList<BatchResult> Results { get { return _results; } }
        public int Total { get { return _items.Count; } }
        /// <summary>0-based index of the item being worked on (Total when finished).</summary>
        public int CurrentIndex { get; private set; }
        public bool IsRunning { get; private set; }
        public bool IsPaused { get; private set; }
        public bool CancelRequested { get; private set; }
        public bool SessionExpired { get; private set; }
        public string CurrentLabel { get; private set; }
        public float CurrentProgress { get; private set; }

        public int DoneCount { get { return Count(BatchStatus.Done); } }
        public int FailedCount { get { return Count(BatchStatus.Failed); } }

        /// <summary>"2/3" while running (the item in progress out of the total), "3/3" once all were handled.</summary>
        public string ProgressText
        {
            get { return Math.Min(CurrentIndex + 1, Total) + "/" + Total; }
        }

        /// <summary>Stops after the current item. While paused it also ends the wait; the paused item is skipped.</summary>
        public void Cancel()
        {
            CancelRequested = true;
            var resume = _resume;
            if (resume != null) resume.TrySetResult(false);
            RaiseChanged();
        }

        /// <summary>Runs every item once, in order. Call once per queue; never throws for an item's failure.</summary>
        public async Task RunAsync()
        {
            if (_started) throw new InvalidOperationException("A batch queue runs once.");
            _started = true;
            IsRunning = true;
            try
            {
                for (var i = 0; i < _items.Count; i++)
                {
                    CurrentIndex = i;
                    if (CancelRequested) { SkipFrom(i, "Cancelled."); return; }
                    if (!await RunItemAsync(i)) { SkipFrom(i + 1, SessionExpired ? "Sign-in expired." : "Cancelled."); return; }
                }
                CurrentIndex = _items.Count;
            }
            finally
            {
                IsRunning = false;
                IsPaused = false;
                CurrentLabel = "";
                RaiseChanged();
            }
        }

        // Returns false when the queue must stop after this item (cancelled while paused, or the session ended).
        private async Task<bool> RunItemAsync(int index)
        {
            var item = _items[index];
            var result = _results[index];
            while (true)
            {
                var retryMark = DeviceLimitNotice.Mark();
                result.Status = BatchStatus.Running;
                CurrentLabel = item.Name;
                CurrentProgress = 0f;
                RaiseChanged();
                try
                {
                    result.Result = await item.Run((label, progress) =>
                    {
                        CurrentLabel = string.IsNullOrEmpty(label) ? item.Name : label;
                        CurrentProgress = progress;
                        RaiseChanged();
                    });
                    result.Status = BatchStatus.Done;
                    result.Message = "";
                    RaiseChanged();
                    return true;
                }
                catch (SessionExpiredException e)
                {
                    SessionExpired = true;
                    result.Status = BatchStatus.Failed;
                    result.Message = e.Message;
                    result.Error = e;
                    RaiseChanged();
                    return false;
                }
                catch (Exception e) when (e is DeviceLimitException || e is ManagerUpdateRequiredException || e is DeviceReleasedException)
                {
                    if (CancelRequested)
                    {
                        // Cancelled while this item ran: there is nothing to wait for (Cancel found no resume to release).
                        result.Status = BatchStatus.Skipped;
                        result.Message = "Cancelled.";
                        RaiseChanged();
                        return false;
                    }
                    var resume = new TaskCompletionSource<bool>();
                    _resume = resume;
                    Func<Task<object>> retry = () => { resume.TrySetResult(true); return Task.FromResult<object>(null); };
                    result.Status = BatchStatus.Paused;
                    result.Message = e.Message;
                    IsPaused = true;
                    // Resuming here retries just this item, then goes on with the rest: the retries this item's services
                    // reported would run it twice, so they go; retries reported earlier (background renewal) stay.
                    DeviceLimitNotice.DropSince(retryMark);
                    DeviceLimitNotice.Report(e, item.Name, retry, null, true);
                    RaiseChanged();
                    var resumed = await resume.Task;
                    _resume = null;
                    IsPaused = false;
                    // Only this queue's retry leaves the notice; a background retry that joined it stays until it is run or cleared.
                    DeviceLimitNotice.Discard(retry);
                    if (!resumed || CancelRequested)
                    {
                        result.Status = BatchStatus.Skipped;
                        result.Message = "Cancelled.";
                        RaiseChanged();
                        return false;
                    }
                    // fall through: run the same item again
                }
                catch (Exception e)
                {
                    result.Status = BatchStatus.Failed;
                    result.Message = e.Message;
                    result.Error = e;
                    RaiseChanged();
                    return true;
                }
            }
        }

        private void SkipFrom(int index, string message)
        {
            for (var i = index; i < _results.Count; i++)
            {
                if (_results[i].Status != BatchStatus.Pending) continue;
                _results[i].Status = BatchStatus.Skipped;
                _results[i].Message = message;
            }
        }

        private int Count(BatchStatus status)
        {
            var n = 0;
            foreach (var r in _results) if (r.Status == status) n++;
            return n;
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
