using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>How urgent a piece of PDF work is. Lower values run first.</summary>
public enum PdfWorkPriority
{
    /// <summary>The user is waiting: opening a file, pages on screen, clicking a link.</summary>
    Visible = 0,

    /// <summary>Soon needed: thumbnails in view, pages just off screen.</summary>
    Nearby = 1,

    /// <summary>Background work: search, printing, prefetching.</summary>
    Background = 2,
}

/// <summary>
/// The one and only thread that is allowed to call into PDFium.
/// </summary>
/// <remarks>
/// PDFium keeps global state and is not thread-safe: two threads calling it at the
/// same time can crash the process. Instead of sprinkling locks everywhere, every
/// PDFium call is posted to this queue and runs here, one at a time.
///
/// The queue is ordered by <see cref="PdfWorkPriority"/> and then by arrival, so the page
/// the user is looking at is never stuck behind 200 thumbnails or a running search.
/// Requests cancelled while waiting are dropped without touching PDFium.
/// </remarks>
#pragma warning disable CA1001 // The thread and its queue live for the whole process; nothing is ever disposed.
internal sealed class PdfiumThread
{
    private readonly PriorityQueue<Action, (int Priority, long Sequence)> _queue = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Thread _thread;
    private long _sequence;

    public PdfiumThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PDFium",
        };
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    /// <summary>Runs <paramref name="work"/> on the PDFium thread and returns its result.</summary>
    public Task<T> InvokeAsync<T>(
        Func<T> work,
        PdfWorkPriority priority = PdfWorkPriority.Visible,
        CancellationToken cancellationToken = default)
    {
        // Already on the PDFium thread (nested call): run inline, queuing would deadlock.
        if (IsCurrentThread)
        {
            return Task.FromResult(work());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _queue.Enqueue(Execute, ((int)priority, _sequence++));
        }

        _available.Release();
        return tcs.Task;

        void Execute()
        {
            // A request may have been cancelled while it waited in the queue
            // (e.g. the user scrolled past the page). Skip it cheaply.
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
    }

    public Task InvokeAsync(
        Action work,
        PdfWorkPriority priority = PdfWorkPriority.Visible,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<bool>(() => { work(); return true; }, priority, cancellationToken);

    private void Run()
    {
        PdfiumNative.FPDF_InitLibrary();
        while (true)
        {
            _available.Wait();
            Action work;
            lock (_lock)
            {
                work = _queue.Dequeue();
            }

            work();
        }
    }
}
#pragma warning restore CA1001
