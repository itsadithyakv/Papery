using Papery.Core.Text;

namespace Papery.Core.Tests;

public sealed class SensitiveTextTests
{
    private static string[] Found(string text, SensitiveKind kinds) =>
        SensitiveText.Find(text, kinds).Select(m => text.Substring(m.Index, m.Length)).ToArray();

    [Fact]
    public void Card_numbers_are_found_in_any_common_spacing()
    {
        string text = "Paid with 4111 1111 1111 1111, then 4111-1111-1111-1111 and 4111111111111111.";

        Assert.Equal(["4111 1111 1111 1111", "4111-1111-1111-1111", "4111111111111111"], Found(text, SensitiveKind.CardNumber));
    }

    [Fact]
    public void A_long_number_that_fails_the_card_check_is_left_alone()
    {
        Assert.Empty(Found("Order 4111 1111 1111 1112 shipped.", SensitiveKind.CardNumber));
    }

    [Fact]
    public void An_IBAN_is_found_only_when_its_check_digits_match()
    {
        Assert.Equal(["GB82 WEST 1234 5698 7654 32"], Found("Pay to GB82 WEST 1234 5698 7654 32 please.", SensitiveKind.BankAccount));
        Assert.Empty(Found("Pay to GB83 WEST 1234 5698 7654 32 please.", SensitiveKind.BankAccount));
    }

    [Fact]
    public void Common_ID_numbers_are_found()
    {
        string text = "SSN 123-45-6789, NI AB 12 34 56 C, PAN ABCPE1234F, Aadhaar 2341 2341 2346.";

        Assert.Equal(["123-45-6789", "AB 12 34 56 C", "ABCPE1234F", "2341 2341 2346"], Found(text, SensitiveKind.IdNumber));
    }

    [Fact]
    public void ID_lookalikes_that_cannot_be_real_are_left_alone()
    {
        // Social Security numbers never start 000, 666 or 9xx; this Aadhaar fails its check digit.
        Assert.Empty(Found("Ref 000-12-3456 and 900-12-3456, and 2341 2341 2347.", SensitiveKind.IdNumber));
    }

    [Fact]
    public void Emails_and_phones_still_work_and_results_come_in_reading_order()
    {
        var found = SensitiveText.Find("Call +44 20 7946 0958 or write to a.b@example.com", SensitiveKind.Email | SensitiveKind.Phone);

        Assert.Equal([SensitiveKind.Phone, SensitiveKind.Email], found.Select(m => m.Kind));
    }

    [Fact]
    public void Nothing_is_found_when_nothing_is_asked_for()
    {
        Assert.Empty(SensitiveText.Find("4111 1111 1111 1111 a.b@example.com", SensitiveKind.None));
    }
}
