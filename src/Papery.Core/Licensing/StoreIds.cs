namespace Papery.Core.Licensing;

/// <summary>
/// ====================================================================================
///  THE ONE PLACE FOR MICROSOFT STORE IDS.
///
///  After you create the "Papery Pro" add-on in Partner Center, copy its 12-character
///  Store ID (looks like 9NBLGGH4R315) from:
///      Partner Center > Apps and games > Papery > Add-ons > Papery Pro > Product identity
///  and paste it below. Then rebuild.
///  (docs/STORE-SETUP.md walks through every step.)
/// ====================================================================================
/// </summary>
public static class StoreIds
{
    /// <summary>The add-on's Store ID, e.g. "9NBLGGH4R315".</summary>
    public const string ProAddOnStoreId = "9ND80Q2KJ618";

    /// <summary>The add-on's Product ID you chose in Partner Center (for reference and logs).</summary>
    public const string ProProductId = "papery_pro";

#if DEBUG || PAPERY_TEST_BUILD
    /// <summary>What Build-StoreUpload.ps1 looks for, to be sure a test build never goes to the Store.</summary>
    public const string TestBuildMarker = "PaperyTestBuildMarker";

    /// <summary>
    /// False in Debug and test builds, which aren't sold through the Store, so Buy simulates a purchase there
    /// and every Pro tool can still be tried. Only the Store upload talks to the real Store.
    /// </summary>
    public static bool IsConfigured => false;
#else
    /// <summary>False until the real Store ID has been pasted in.</summary>
    public static bool IsConfigured =>
        ProAddOnStoreId.Length == 12 && !ProAddOnStoreId.Contains("PASTE", StringComparison.Ordinal);
#endif
}
