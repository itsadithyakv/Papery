namespace Papery.Services;

/// <summary>
/// Runs fire-and-forget UI work (button clicks, drops) so that a bug is logged and explained
/// instead of closing Papery. An exception escaping an "async void" handler can end the
/// process without reaching Application.UnhandledException, so handlers go through here.
/// </summary>
internal static class Guard
{
    /// <summary>Raised on the UI thread with a short description, so the view can say something kind.</summary>
    public static event EventHandler<string>? Failed;

    public static async void Run(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error($"{what} failed", ex);
            Failed?.Invoke(null, what);
        }
    }
}
