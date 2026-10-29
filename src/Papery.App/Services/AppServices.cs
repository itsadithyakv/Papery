using Papery.Core.Storage;
using Windows.Storage;

namespace Papery.Services;

/// <summary>
/// App-wide services, created once at start-up. Kept deliberately simple (no DI container)
/// so it's easy to follow: every service is a property here.
/// </summary>
internal static class AppServices
{
    /// <summary>Papery's private folder on this PC. Removed by Windows when Papery is uninstalled.</summary>
    public static string DataFolder { get; private set; } = string.Empty;

    public static SettingsStore Settings { get; private set; } = null!;

    public static ViewStateStore ViewState { get; private set; } = null!;

    public static void Initialize()
    {
        DataFolder = GetDataFolder();
        Directory.CreateDirectory(DataFolder);
        Log.Initialize(Path.Combine(DataFolder, "logs"));
        Settings = new SettingsStore(Path.Combine(DataFolder, "settings.json"));
        ViewState = new ViewStateStore(Path.Combine(DataFolder, "state.json"));
    }

    private static string GetDataFolder()
    {
        try
        {
            return ApplicationData.Current.LocalFolder.Path; // packaged (normal) case
        }
        catch (InvalidOperationException)
        {
            // Running without package identity (e.g. a test harness).
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Papery");
        }
    }
}
