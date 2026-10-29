# Papery – Microsoft Store setup

Plain-language steps. Parts 1–3 are needed now (so purchases and licence checks work);
the rest of the publishing guide (payout and tax, price, upload, hidden release, promo codes,
certification) is completed in the final stage.

---

## Part 1 – Associate the app with your reserved name "Papery"

Licence checks and purchases only work when the app's package identity matches the name you
reserved in Partner Center. Until then, Papery runs as the development package `Papery.Dev`
and the Store can't answer for it.

**Option A – Visual Studio (recommended)**

1. Install **Visual Studio Community** with the **WinUI application development** workload.
2. Open `D:\Papery\Papery.slnx`.
3. In **Solution Explorer**, right-click **Papery.App** › **Package and Publish** › **Associate App with the Store…**
4. Sign in with the Microsoft account you use for Partner Center.
5. Pick **Papery** from the list of reserved names › **Next** › **Associate**.
6. Visual Studio updates `src/Papery.App/Package.appxmanifest` (the `<Identity Name=… Publisher=…>`
   and display names) and adds a `Package.StoreAssociation.xml` file. Commit both.

**Option B – by hand (no Visual Studio)**

1. Partner Center › **Apps and games** › **Papery** › **Product management** › **Product identity**.
2. Copy **Package/Identity/Name**, **Package/Identity/Publisher** and **Package/Properties/PublisherDisplayName**.
3. In `src/Papery.App/Package.appxmanifest`, replace:
   - `Name="Papery.Dev"` with the Identity Name,
   - `Publisher="CN=Papery Dev"` with the Identity Publisher,
   - `<PublisherDisplayName>Papery</PublisherDisplayName>` with your publisher display name.
4. In `tools/Run-Papery.ps1`, replace the two occurrences of `Papery.Dev` with the new Identity Name.

> After associating, uninstall the old development copy once (Start › right-click Papery › Uninstall),
> then run `tools/Run-Papery.ps1` again.

---

## Part 2 – Create the "Papery Pro" add-on

1. Partner Center › **Apps and games** › **Papery** › **Add-ons** › **Create a new add-on**.
2. Product type: **Durable**.
3. Product ID: **papery_pro** (exactly this; it can't be changed later).
4. **Properties**: Product lifetime **Forever**. For Content type, pick the closest match to
   "unlocks features in the app" (usually **Electronic software download**).
5. **Pricing and availability**: base price **USD 9.99**. The Store converts it for each market.
6. **Age ratings**: answer the questionnaire (no violence, no user interaction, no purchases of
   physical goods); the result should match the app's rating.
7. **Store listing** (English): paste the text below.
8. **Submit** the add-on. It goes through certification with (or before) the app.

Ready-to-paste add-on listing:

- **Title:** Papery Pro
- **Description:**
  Unlock every tool in Papery with one payment: organise, merge and split pages; crop and resize;
  draw, stamp and edit; sign; convert images and pages; make scans searchable with OCR; compress;
  protect with passwords; permanently redact; and run batch actions on many files.
  One payment. No subscription. Ever. Works on all your PCs signed in with the same Microsoft account.

---

## Part 3 – Paste the add-on's Store ID into the code

1. Partner Center › **Papery** › **Add-ons** › **Papery Pro** › **Product identity**.
2. Copy the **Store ID**: 12 characters, starting with 9, for example `9NBLGGH4R315`.
3. Open **`src/Papery.Core/Licensing/StoreIds.cs`** and replace

   ```csharp
   public const string ProAddOnStoreId = "PASTE_STORE_ID_HERE";
   ```

   with your ID, e.g.

   ```csharp
   public const string ProAddOnStoreId = "9NBLGGH4R315";
   ```

4. Rebuild (`tools/Run-Papery.ps1` or Visual Studio). That's the only place the ID lives.

### How Papery handles the purchase

| Situation | What Papery does |
|---|---|
| Start-up | Pro unlocks at once if this PC has a confirmed purchase (encrypted record); the Store is asked quietly in the background |
| Offline / Store slow | Nothing changes. A paying user is never shown locks |
| Bought on another PC | The background check (or **Settings › Restore purchase**) finds it |
| Purchase cancelled | "No problem. Nothing was charged, and your work is just as you left it." |
| Refund | Pro is removed only after the Store says "not owned" on 3 separate days |

### Testing both states before publishing

- **Debug builds** have **Settings › Developer › Pretend licence** (Real / Pretend Free / Pretend Pro).
  With the Store ID still a placeholder, **Buy** simulates a purchase so you can see the Welcome screen.
- **Release builds** (what you upload) contain none of this: it is removed by the compiler (`#if DEBUG`).
- To test a **real** purchase safely, publish the app as **hidden** first and buy with a promo code.
  This is covered step by step in the full guide (final stage).
