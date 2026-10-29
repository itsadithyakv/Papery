using Papery.Core.Licensing;
using Windows.Services.Store;
using WinRT.Interop;

namespace Papery.Services.Licensing;

/// <summary>How a purchase attempt ended, in words the UI can explain.</summary>
public enum PurchaseOutcome
{
    Purchased,
    AlreadyOwned,
    Cancelled,
    NetworkProblem,
    StoreProblem,

    /// <summary>The Store ID hasn't been pasted into StoreIds.cs yet (development builds).</summary>
    NotConfigured,
}

/// <summary>
/// Talks to the Microsoft Store: is Papery Pro owned, what does it cost here, buy it.
/// These are the only network calls Papery ever makes, and Windows makes them, not Papery.
/// </summary>
internal sealed class StoreService
{
    private static readonly string[] DurableKind = ["Durable"];
    private StoreContext? _context;

    /// <summary>Raised when Windows tells us licences changed (bought on another PC, refunded...).</summary>
    public event EventHandler? LicensesChanged;

    /// <summary>
    /// Must be called once with the main window: on desktop, the Store's purchase window needs
    /// a parent window to appear over.
    /// </summary>
    public void Initialize(nint ownerWindow)
    {
        if (_context != null)
        {
            return;
        }

        try
        {
            _context = StoreContext.GetDefault();
            InitializeWithWindow.Initialize(_context, ownerWindow);
            _context.OfflineLicensesChanged += (_, _) => LicensesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("The Microsoft Store isn't available", ex);
            _context = null;
        }
    }

    /// <summary>
    /// Checks ownership. "NotOwned" is only returned after the Store answered online; anything
    /// uncertain is "Unknown", which never changes the user's Pro status.
    /// </summary>
    public async Task<StoreCheckResult> CheckAsync()
    {
        if (_context == null || !StoreIds.IsConfigured)
        {
            return StoreCheckResult.Unknown;
        }

        try
        {
            // 1) The licence Windows keeps on this PC (works offline).
            var license = await _context.GetAppLicenseAsync();
            if (license?.AddOnLicenses.Any(a => a.Key.StartsWith(StoreIds.ProAddOnStoreId, StringComparison.OrdinalIgnoreCase) && a.Value.IsActive) == true)
            {
                return StoreCheckResult.Owned;
            }

            // 2) Ask the Store for the user's collection (needs the internet).
            var collection = await _context.GetUserCollectionAsync(DurableKind);
            if (collection.ExtendedError != null)
            {
                return StoreCheckResult.Unknown;
            }

            return collection.Products.TryGetValue(StoreIds.ProAddOnStoreId, out var product) && product.IsInUserCollection
                ? StoreCheckResult.Owned
                : StoreCheckResult.NotOwned;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Store licence check failed", ex);
            return StoreCheckResult.Unknown;
        }
    }

    /// <summary>The price in the user's own currency, as the Store formats it (e.g. "₹849.00"), or null.</summary>
    public async Task<string?> GetPriceAsync()
    {
        if (_context == null || !StoreIds.IsConfigured)
        {
            return null;
        }

        try
        {
            var result = await _context.GetStoreProductsAsync(DurableKind, [StoreIds.ProAddOnStoreId]);
            return result.ExtendedError == null && result.Products.TryGetValue(StoreIds.ProAddOnStoreId, out var product)
                ? product.Price.FormattedPrice
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Reading the Pro price failed", ex);
            return null;
        }
    }

    /// <summary>Opens the Store's own purchase window. Papery never sees payment details.</summary>
    public async Task<PurchaseOutcome> PurchaseAsync()
    {
        if (!StoreIds.IsConfigured)
        {
            return PurchaseOutcome.NotConfigured;
        }

        if (_context == null)
        {
            return PurchaseOutcome.StoreProblem;
        }

        try
        {
            var result = await _context.RequestPurchaseAsync(StoreIds.ProAddOnStoreId);
            if (result.ExtendedError != null)
            {
                Log.Info($"Purchase ended with {result.Status}: {result.ExtendedError.Message}");
            }

            return result.Status switch
            {
                StorePurchaseStatus.Succeeded => PurchaseOutcome.Purchased,
                StorePurchaseStatus.AlreadyPurchased => PurchaseOutcome.AlreadyOwned,
                StorePurchaseStatus.NotPurchased => PurchaseOutcome.Cancelled,
                StorePurchaseStatus.NetworkError => PurchaseOutcome.NetworkProblem,
                _ => PurchaseOutcome.StoreProblem,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Purchase failed", ex);
            return PurchaseOutcome.StoreProblem;
        }
    }
}
