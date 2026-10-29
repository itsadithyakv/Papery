using Papery.Core.Licensing;
using Papery.Core.Storage;
using Papery.Services.Licensing;
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

    /// <summary>Is Papery Pro unlocked? (Offline-safe: see ProEntitlement.)</summary>
    public static ProEntitlement Pro { get; private set; } = null!;

    public static StoreService Store { get; } = new();

    public static void Initialize()
    {
        DataFolder = GetDataFolder();
        Directory.CreateDirectory(DataFolder);
        Log.Initialize(Path.Combine(DataFolder, "logs"));
        Settings = new SettingsStore(Path.Combine(DataFolder, "settings.json"));
        ViewState = new ViewStateStore(Path.Combine(DataFolder, "state.json"));
        Pro = new ProEntitlement(Path.Combine(DataFolder, "license.bin"), new DpapiProtector());
#if DEBUG
        // Developer builds only: the Settings switch to pretend Free or Pro. Not compiled into Store builds.
        Pro.SetDebugMode(Settings.Current.DebugLicenseMode);
#endif
    }

    /// <summary>
    /// Connects the Store to the first window and re-checks the licence quietly in the background.
    /// Pro already works from the local record before this finishes.
    /// </summary>
    public static void StartLicenseChecks(nint ownerWindow)
    {
        Store.Initialize(ownerWindow);
        Store.LicensesChanged += async (_, _) => Pro.Apply(await Store.CheckAsync());
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)); // don't compete with opening the first file
            Pro.Apply(await Store.CheckAsync());
        });
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
