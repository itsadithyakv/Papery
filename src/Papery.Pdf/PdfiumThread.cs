using System.Collections.Concurrent;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>
/// The one and only thread that is allowed to call into PDFium.
/// </summary>
/// <remarks>
/// PDFium keeps global state and is not thread-safe: two threads calling it at the
/// same time can crash the process. Instead of sprinkling locks everywhere, every
/// PDFium call is posted to this queue and runs here, one at a time.
///
/// Stage a will turn the plain FIFO queue into a priority queue
/// (visible pages first, cancelled requests skipped).
/// </remarks>
#pragma warning disable CA1001 // The thread and its queue live for the whole process; nothing is ever disposed.
internal sealed class PdfiumThread
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

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
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(Execute, CancellationToken.None);
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

    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default) =>
        InvokeAsync<bool>(() => { work(); return true; }, cancellationToken);

    private void Run()
    {
        PdfiumNative.FPDF_InitLibrary();
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }
}
#pragma warning restore CA1001
