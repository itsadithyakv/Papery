# Papery – Microsoft Store setup

Plain-language steps, in the order to do them: **Part 4** (payout and tax) first, as nothing can sell without it, then Parts 1, 2, 3, 11, 5, 6, 7, 8. Parts 9 and 10 are for reference.

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
  This is covered step by step in Part 7 below.

---

## Part 4 – Set up payouts and tax (do this before anything else can sell)

You can't sell the add-on until both of these are finished. Allow a few days, because Microsoft may check your details.

1. Go to [partner.microsoft.com/dashboard](https://partner.microsoft.com/dashboard) and sign in.
2. Click the **gear icon** › **Account settings** › **Payout and tax** › **Payout and tax profiles**.
3. **Payout profile**: choose bank transfer, and enter your bank's details exactly as your bank shows them.
4. **Tax profile**: answer the questions for your country. If you're in India, have your PAN ready. If you sell in the US, complete the W-8BEN form it offers, which can reduce US withholding tax.
5. Wait until both show **Complete** or **Approved**.

Microsoft pays you once your balance passes its payment threshold. It keeps a share (15% on apps at the time of writing; check **Agreements** in Partner Center for the current rate).

## Part 5 – Set the app's price to Free

1. Partner Center › **Apps and games** › **Papery** › **Start your submission**.
2. **Pricing and availability** › **Base price**: choose **Free**.
3. Leave **Free trial** as **No free trial**, since the free version is the trial.
4. **Markets**: leave all selected unless you have a reason not to.
5. **Visibility**: choose **Hide this product in the Store** and **Direct link only** for now (see Part 7).
6. **Save**.

## Part 6 – Build the package and upload it

Before building, Parts 1 to 3 must be done: the app associated with your reserved name, and the add-on's Store ID pasted in.

Also set the version: in `src/Papery.App/Package.appxmanifest`, set `Version="1.0.0.0"` (the last number must stay 0). Each later upload needs a higher version.

**With Visual Studio (easiest)**

1. Open `Papery.slnx`, right-click **Papery.App** › **Package and Publish** › **Create App Packages**.
2. Choose **Microsoft Store**, pick **Papery**, **Next**.
3. Tick **x64** and **ARM64**, **Release**. Output: **Always** generate an app bundle. **Create**.
4. When it finishes, choose **Launch Windows App Certification Kit** and run it. Fix anything it fails.
5. You'll get an `.msixupload` file in `AppPackages`.

**Upload**

1. Your submission › **Packages** › drag in the `.msixupload` file.
2. Wait for validation. Warnings about `runFullTrust` are expected: it's a normal desktop app. You'll explain it in the next step.
3. **Save**.

**Fill in the rest of the submission**

1. **Properties**: Category **Productivity**. Privacy policy URL: see Part 11. Under **Product declarations**, tick that it uses the camera only if asked about it, and answer honestly. It doesn't use personal data.
2. **Age ratings**: complete the questionnaire. It should come out as suitable for everyone.
3. **Store listings** › **English**: paste from `branding/listing/store-listing.md`, and add the art and screenshots described in `branding/README.md`.
4. **Submission options** › **Notes for certification**: paste the notes from `branding/listing/store-listing.md`, which explain `runFullTrust` and the camera.
5. **Submit to the Store**.

The test packages in `dist/` aren't for uploading. They're signed with a test certificate, and the Store needs its own package made in the steps above.

## Part 7 – Publish hidden first, test a real purchase, then go public

1. Keep the visibility from Part 5: **Hide this product in the Store**, **Direct link only**. Nobody can find it by searching.
2. Once it passes certification, open **Product identity** and copy the **URL for your Microsoft Store listing**.
3. Open that link on your PC and install Papery from the Store (uninstall any test copy first).
4. Make a promo code (Part 8), or really buy Pro. You can refund a real purchase.
5. Check these:
   - The price shows correctly on the unlock screen.
   - Buying unlocks everything, and the Welcome screen appears.
   - Turn off Wi-Fi and restart: Pro stays unlocked.
   - On a second PC with the same Microsoft account: **Settings › Restore purchase** unlocks Pro.
6. When you're happy: **Update** the submission, **Pricing and availability** › **Visibility**, choose **Make this product available and discoverable in the Store**, and **Submit**.

## Part 8 – Promo codes, so testers get Pro for free

1. Partner Center › **Papery** › **Add-ons** › **Papery Pro** › **Promo codes** (under Engage or Marketing, depending on the current layout) › **Create**.
2. Name the order, e.g. "Testers". Type: **One-time-use**. Quantity: as many as you need. Choose an end date.
3. Wait for the codes (they can take up to a day), then download the file.
4. Each tester opens `https://www.microsoft.com/redeem`, signs in, and enters a code. Pro appears when they next open Papery, or after **Settings › Restore purchase**.

Microsoft limits how many codes you can make per product, so don't make more than you need.

## Part 9 – What the listing must say about the in-app purchase

Certification expects people to know what they get free and what costs money. The listing text in `branding/listing/store-listing.md` already covers this:

- The headline says the price and that it's one payment.
- It lists what's free, then what's in Pro.
- The Store adds **"Offers in-app purchases"** by itself once the add-on exists.

Don't say "free" without mentioning Pro, and don't list Pro features as if they were free. Keep the description honest about limits such as Word export.

## Part 10 – If certification fails

1. Partner Center › **Papery** › your submission › **View report** (or the link in Microsoft's email).
2. Each problem has a policy number and a description. Read them, and look at any screenshot they attached.
3. Common ones, and what to do:

| What it says | What to do |
|---|---|
| **10.1 / metadata**: the listing doesn't match the app | Make the description match what the app does, and check the screenshots are current |
| **10.8.5 / in-app purchase**: price or terms unclear | Make sure the listing says what's in Papery Pro and that it's a one-time payment |
| **10.2.1 / crash on launch** | Run the Windows App Certification Kit on the same package, and test on a clean PC or virtual machine |
| **Privacy policy** missing or wrong | Check the link opens without signing in, and that it mentions the camera |
| **Restricted capability: runFullTrust** needs a reason | Paste the certification notes again; they explain it's a desktop app |
| **Product name** problems | The name must be the one you reserved. Don't add "Pro" or "free" to it |

4. Fix it, raise the version in `Package.appxmanifest` if the package changed, and **Update** then **Submit** again. A note to the testers in **Notes for certification** helps.
5. If you think they got it wrong, reply through **Partner Center** › **Help + support**.

## Part 11 – Host the privacy policy

The Store needs a web address for it. The free way, using the GitHub repo you already have:

1. Open `branding/listing/privacy-policy.html`, replace both `YOUR-SUPPORT-EMAIL` with your real address, save, and push.
2. In GitHub, make a new **public** repository called `papery-privacy` (your main repo is private or holds code you may not want public).
3. Upload the file there, renamed `index.html`.
4. In that repository: **Settings › Pages** › **Deploy from a branch** › **main** › **Save**.
5. After a minute, your page is at `https://itsadithyakv.github.io/papery-privacy/`. Open it to check, then paste it into **Properties** and the Store listing.

## Part 12 – Later updates

1. Make your changes and raise `Version` in `Package.appxmanifest` (1.0.1.0, 1.1.0.0, and so on).
2. Create App Packages again, as in Part 6.
3. Partner Center › **Papery** › **Update**, replace the package, **Submit**.

People who have Pro keep it through every update.
