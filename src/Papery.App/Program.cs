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
        AppActivationArguments activation;
        try
        {
            activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true; // nothing to hand over that can be read; the open Papery carries on as it was
        }

        bool handed = false;
        var done = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            try
            {
                await mainInstance.RedirectActivationToAsync(activation);
                handed = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The other Papery was closing, or didn't answer.
            }
            finally
            {
                done.Set();
            }
        });

        if (done.Wait(TimeSpan.FromSeconds(10)) && handed)
        {
            return true;
        }

        // Rather than quit and leave the person with nothing, open the file here. If the other Papery has gone,
        // this one takes over as the one later launches hand their files to.
        _ = AppInstance.FindOrRegisterForKey("papery-main");
        return false;
    }
}
