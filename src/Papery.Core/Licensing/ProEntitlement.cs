using System.Text.Json;
using System.Text.Json.Serialization;

namespace Papery.Core.Licensing;

/// <summary>What one licence check with the Microsoft Store said.</summary>
public enum StoreCheckResult
{
    /// <summary>The Store confirmed this user owns Papery Pro.</summary>
    Owned,

    /// <summary>The Store answered and says Papery Pro is not owned (or was refunded).</summary>
    NotOwned,

    /// <summary>No real answer: offline, Store busy, not set up yet. Never changes anything.</summary>
    Unknown,
}

/// <summary>For developer builds only: pretend to be Free or Pro to test both.</summary>
public enum DebugLicenseMode
{
    Real,
    ForceFree,
    ForcePro,
}

/// <summary>Encrypts the local purchase record (DPAPI in the app; a fake in tests).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Decides whether Papery Pro is unlocked. Built so a paying customer is never locked out:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A confirmed purchase is remembered on this PC (encrypted), so Pro works at once on launch,
/// offline, or when the Store is slow.</item>
/// <item>"Unknown" answers (offline, errors) never change anything.</item>
/// <item>Pro is only taken away after the Store says "not owned" on <see cref="DaysBeforeRevoking"/>
/// different days of successful checks: what a real refund looks like, and what a one-off Store
/// glitch never does.</item>
/// </list>
/// Pure logic plus one small file, so every rule is covered by unit tests.
/// </remarks>
public sealed class ProEntitlement
{
    public const int DaysBeforeRevoking = 3;

    private readonly string _cachePath;
    private readonly ISecretProtector _protector;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _lock = new();
    private CacheData _cache;
    private DebugLicenseMode _debugMode;

    public ProEntitlement(string cachePath, ISecretProtector protector, Func<DateTimeOffset>? clock = null)
    {
        _cachePath = cachePath;
        _protector = protector;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _cache = Load();
    }

    /// <summary>Raised whenever <see cref="IsPro"/> may have changed.</summary>
    public event EventHandler? Changed;

    public bool IsPro
    {
        get
        {
            lock (_lock)
            {
                return _debugMode switch
                {
                    DebugLicenseMode.ForcePro => true,
                    DebugLicenseMode.ForceFree => false,
                    _ => _cache.Owned,
                };
            }
        }
    }

    /// <summary>When the purchase was first confirmed on this PC (null if never).</summary>
    public DateTimeOffset? ConfirmedOn
    {
        get
        {
            lock (_lock)
            {
                return _cache.Owned ? _cache.FirstConfirmed : null;
            }
        }
    }

    public DebugLicenseMode DebugMode
    {
        get
        {
            lock (_lock)
            {
                return _debugMode;
            }
        }
    }

    /// <summary>Developer builds only (the app calls this inside #if DEBUG).</summary>
    public void SetDebugMode(DebugLicenseMode mode)
    {
        lock (_lock)
        {
            _debugMode = mode;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Feeds in what the Store said. Returns true if Pro status changed.</summary>
    public bool Apply(StoreCheckResult result)
    {
        bool before = IsPro;
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(_now().UtcDateTime);
            switch (result)
            {
                case StoreCheckResult.Owned:
                    _cache = new CacheData
                    {
                        Owned = true,
                        FirstConfirmed = _cache.Owned && _cache.FirstConfirmed != null ? _cache.FirstConfirmed : _now(),
                        LastConfirmed = _now(),
                        NotOwnedDays = [],
                    };
                    break;

                case StoreCheckResult.NotOwned when _cache.Owned:
                    // Only count distinct days, so many checks on one bad day count once.
                    if (!_cache.NotOwnedDays.Contains(today))
                    {
                        _cache.NotOwnedDays.Add(today);
                    }

                    if (_cache.NotOwnedDays.Count >= DaysBeforeRevoking)
                    {
                        _cache = new CacheData(); // refunded: back to Free
                    }

                    break;

                default:
                    return false; // Unknown, or NotOwned for someone who never had Pro: nothing to do
            }

            Save();
        }

        bool after = IsPro;
        if (before != after)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return before != after;
    }

    private CacheData Load()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                byte[] json = _protector.Unprotect(File.ReadAllBytes(_cachePath));
                return JsonSerializer.Deserialize(json, LicensingJsonContext.Default.CacheData) ?? new CacheData();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            // Unreadable or tampered: treat as no record. The Store check will confirm a real purchase again.
        }

        return new CacheData();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            byte[] data = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(_cache, LicensingJsonContext.Default.CacheData));
            string temp = _cachePath + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (IOException)
        {
            // The in-memory state is still right for this session; the next confirmed check saves again.
        }
    }

    internal sealed class CacheData
    {
        public bool Owned { get; set; }

        public DateTimeOffset? FirstConfirmed { get; set; }

        public DateTimeOffset? LastConfirmed { get; set; }

        public List<DateOnly> NotOwnedDays { get; set; } = [];
    }
}

[JsonSerializable(typeof(ProEntitlement.CacheData))]
internal sealed partial class LicensingJsonContext : JsonSerializerContext;
