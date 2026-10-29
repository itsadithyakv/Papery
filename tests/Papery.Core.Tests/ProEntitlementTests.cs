using Papery.Core.Licensing;

namespace Papery.Core.Tests;

public sealed class ProEntitlementTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("papery-licence-").FullName;
    private DateTimeOffset _now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private string CachePath => Path.Combine(_folder, "license.bin");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ProEntitlement Create() => new(CachePath, new XorProtector(), () => _now);

    [Fact]
    public void New_install_is_free()
    {
        Assert.False(Create().IsPro);
    }

    [Fact]
    public void Confirmed_purchase_unlocks_and_is_remembered_offline()
    {
        var first = Create();
        Assert.True(first.Apply(StoreCheckResult.Owned));
        Assert.True(first.IsPro);

        // Next launch, no network: the Store can't answer, but Pro stays.
        var nextLaunch = Create();
        Assert.True(nextLaunch.IsPro);
        Assert.False(nextLaunch.Apply(StoreCheckResult.Unknown));
        Assert.True(nextLaunch.IsPro);
    }

    [Fact]
    public void Unknown_answers_never_change_anything()
    {
        var free = Create();
        free.Apply(StoreCheckResult.Unknown);
        Assert.False(free.IsPro);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public void One_bad_store_answer_never_locks_out_a_paying_user()
    {
        var pro = Create();
        pro.Apply(StoreCheckResult.Owned);

        // A glitchy day: many "not owned" answers on the same day count once.
        for (int i = 0; i < 10; i++)
        {
            pro.Apply(StoreCheckResult.NotOwned);
        }

        _now = _now.AddDays(1);
        pro.Apply(StoreCheckResult.NotOwned);

        Assert.True(pro.IsPro);
        Assert.True(Create().IsPro); // and it survives a restart
    }

    [Fact]
    public void A_refund_is_respected_after_three_separate_days()
    {
        var pro = Create();
        pro.Apply(StoreCheckResult.Owned);

        for (int day = 0; day < ProEntitlement.DaysBeforeRevoking; day++)
        {
            Assert.True(pro.IsPro);
            pro.Apply(StoreCheckResult.NotOwned);
            _now = _now.AddDays(1);
        }

        Assert.False(pro.IsPro);
        Assert.False(Create().IsPro);
    }

    [Fact]
    public void An_owned_answer_resets_the_not_owned_count()
    {
        var pro = Create();
        pro.Apply(StoreCheckResult.Owned);
        pro.Apply(StoreCheckResult.NotOwned);
        _now = _now.AddDays(1);
        pro.Apply(StoreCheckResult.NotOwned);
        _now = _now.AddDays(1);
        pro.Apply(StoreCheckResult.Owned); // the Store recovered
        _now = _now.AddDays(1);
        pro.Apply(StoreCheckResult.NotOwned);

        Assert.True(pro.IsPro);
    }

    [Fact]
    public void Corrupt_or_tampered_cache_means_free_without_crashing()
    {
        File.WriteAllBytes(CachePath, [1, 2, 3, 4, 5]);

        Assert.False(Create().IsPro);
    }

    [Fact]
    public void A_copied_cache_from_another_account_does_not_unlock()
    {
        var pro = Create();
        pro.Apply(StoreCheckResult.Owned);

        // Another Windows account has a different key, so the file can't be read.
        var otherAccount = new ProEntitlement(CachePath, new XorProtector(key: 0x5A), () => _now);
        Assert.False(otherAccount.IsPro);
    }

    [Fact]
    public void Debug_switch_overrides_both_ways_without_touching_the_real_record()
    {
        var entitlement = Create();
        int changes = 0;
        entitlement.Changed += (_, _) => changes++;

        entitlement.SetDebugMode(DebugLicenseMode.ForcePro);
        Assert.True(entitlement.IsPro);

        entitlement.SetDebugMode(DebugLicenseMode.ForceFree);
        entitlement.Apply(StoreCheckResult.Owned);
        Assert.False(entitlement.IsPro);

        entitlement.SetDebugMode(DebugLicenseMode.Real);
        Assert.True(entitlement.IsPro);
        Assert.True(changes >= 3);
    }

    [Fact]
    public void First_confirmation_date_is_kept_across_rechecks()
    {
        var pro = Create();
        pro.Apply(StoreCheckResult.Owned);
        var first = pro.ConfirmedOn;
        _now = _now.AddDays(30);
        pro.Apply(StoreCheckResult.Owned);

        Assert.Equal(first, pro.ConfirmedOn);
    }

    /// <summary>Stand-in for DPAPI: a different key behaves like a different Windows account.</summary>
    private sealed class XorProtector(byte key = 0xA7) : ISecretProtector
    {
        private const byte Marker = 0x50;

        public byte[] Protect(byte[] data) => [Marker, .. data.Select(b => (byte)(b ^ key))];

        public byte[] Unprotect(byte[] data)
        {
            if (data.Length == 0 || data[0] != Marker)
            {
                throw new System.Security.Cryptography.CryptographicException("Not ours.");
            }

            var plain = data.Skip(1).Select(b => (byte)(b ^ key)).ToArray();
            if (plain.Length == 0 || plain[0] != (byte)'{')
            {
                throw new System.Security.Cryptography.CryptographicException("Wrong key.");
            }

            return plain;
        }
    }
}
