namespace Papery.Core.Licensing;

/// <summary>
/// ====================================================================================
///  THE ONE PLACE FOR MICROSOFT STORE IDS.
///
///  After you create the "Papery Pro" add-on in Partner Center, copy its 12-character
///  Store ID (looks like 9NBLGGH4R315) from:
///      Partner Center > Apps and games > Papery > Add-ons > Papery Pro > Product identity
///  and paste it below in place of PASTE_STORE_ID_HERE. Then rebuild.
///  (docs/STORE-SETUP.md walks through every step.)
/// ====================================================================================
/// </summary>
public static class StoreIds
{
    /// <summary>The add-on's Store ID, e.g. "9NBLGGH4R315".</summary>
    public const string ProAddOnStoreId = "PASTE_STORE_ID_HERE";

    /// <summary>The add-on's Product ID you chose in Partner Center (for reference and logs).</summary>
    public const string ProProductId = "papery_pro";

    /// <summary>False until the real Store ID has been pasted in.</summary>
    public static bool IsConfigured =>
        ProAddOnStoreId.Length == 12 && !ProAddOnStoreId.Contains("PASTE", StringComparison.Ordinal);
}
