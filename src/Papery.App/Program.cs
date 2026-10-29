using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Papery;

/// <summary>
/// Custom entry point. Papery runs as a single instance: double-clicking a second PDF
/// sends it to the window that is already open (as a new tab) instead of starting
/// another copy of the app.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToRunningInstance())
        {
            return 0;
        }

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <returns>True if another Papery is running and has been handed this activation.</returns>
    private static bool RedirectToRunningInstance()
    {
        var mainInstance = AppInstance.FindOrRegisterForKey("papery-main");
        if (mainInstance.IsCurrent)
        {
            return false;
        }

        // RedirectActivationToAsync must not block the STA thread's message pump,
        // so it runs on a worker thread while this thread waits.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var done = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            try
            {
                await mainInstance.RedirectActivationToAsync(activation);
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(TimeSpan.FromSeconds(10));
        return true;
    }
}
