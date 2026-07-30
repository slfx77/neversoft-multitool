namespace NeversoftMultitool;

/// <summary>
///     A live handle on one long-running operation reported in the status bar.
///     Dispose marks the operation complete (idempotent, callable from any
///     thread); <see cref="IProgress{T}.Report" /> takes a 0..1 fraction.
/// </summary>
internal interface IGlobalProgressScope : IDisposable, IProgress<double>
{
    void Report(int current, int total);
    void SetIndeterminate(bool indeterminate);
    void SetLabel(string label);
}

/// <summary>Immutable view of the displayed operation for the status bar.</summary>
internal sealed record GlobalProgressSnapshot(string Label, double Fraction, bool Indeterminate);

/// <summary>
///     App-wide progress reporting into the MainWindow status bar. Operations
///     call <see cref="Begin" /> and dispose the returned scope when done; the
///     bar shows the most recently begun still-active operation. All calls
///     self-marshal to the UI thread and degrade to no-ops when no window
///     exists, so worker threads can report directly.
/// </summary>
internal static class GlobalProgress
{
    private static readonly object Sync = new();

    // Active operations in begin order; the displayed one is the last. Dispose
    // removes by reference from anywhere in the list, so an older operation's
    // async finally disposing AFTER a newer Begin cannot hide the newer op.
    private static readonly List<Scope> Active = [];
    private static int _flushQueued;

    public static IGlobalProgressScope Begin(string label, bool indeterminate = false)
    {
        var scope = new Scope(label, indeterminate);
        lock (Sync)
        {
            Active.Add(scope);
        }

        RequestFlush();
        return scope;
    }

    /// <summary>
    ///     Queues at most one Flush at a time onto the UI thread; no-op when the
    ///     window is gone (shutdown) or the dispatcher rejects the callback.
    /// </summary>
    private static void RequestFlush()
    {
        if (Interlocked.Exchange(ref _flushQueued, 1) == 1)
            return;

        var dispatcher = MainWindow.Instance?.DispatcherQueue;
        if (dispatcher == null || !dispatcher.TryEnqueue(Flush))
            Interlocked.Exchange(ref _flushQueued, 0);
    }

    private static void Flush()
    {
        Interlocked.Exchange(ref _flushQueued, 0);

        GlobalProgressSnapshot? snapshot;
        lock (Sync)
        {
            snapshot = Active.Count > 0 ? Active[^1].TakeSnapshot() : null;
        }

        MainWindow.Instance?.ApplyGlobalProgress(snapshot);
    }

    private sealed class Scope(string label, bool indeterminate) : IGlobalProgressScope
    {
        private int _disposed;
        private volatile bool _indeterminate = indeterminate;
        private volatile string _label = label;
        private int _permille;

        public void Report(double value)
        {
            var permille = (int)Math.Clamp(value * 1000.0, 0.0, 1000.0);
            if (Interlocked.Exchange(ref _permille, permille) == permille)
                return;

            if (IsDisplayed())
                RequestFlush();
        }

        public void Report(int current, int total)
        {
            Report(total <= 0 ? 0.0 : (double)current / total);
        }

        public void SetIndeterminate(bool indeterminate)
        {
            _indeterminate = indeterminate;
            RequestFlush();
        }

        public void SetLabel(string label)
        {
            _label = label;
            RequestFlush();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            lock (Sync)
            {
                Active.Remove(this);
            }

            RequestFlush();
        }

        public GlobalProgressSnapshot TakeSnapshot()
        {
            return new GlobalProgressSnapshot(_label, Volatile.Read(ref _permille) / 1000.0, _indeterminate);
        }

        private bool IsDisplayed()
        {
            lock (Sync)
            {
                return Active.Count > 0 && ReferenceEquals(Active[^1], this);
            }
        }
    }
}
