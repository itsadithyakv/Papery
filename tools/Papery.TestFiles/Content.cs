namespace Papery.TestFiles;

/// <summary>Plain English filler text. Real sentences make OCR and search tests meaningful.</summary>
internal static class Content
{
    public static readonly string[] Paragraphs =
    [
        "Paper has outlived every prediction of its demise. Offices that promised to go paperless still print contracts, invoices and boarding passes, and most of those documents travel as PDF files long before anyone holds them.",
        "A good document tool should feel like a sharp pencil: always ready, never in the way. It opens quickly, shows the page clearly and lets you get on with the job, whether that is signing a lease or checking a drawing.",
        "This paragraph exists to give the search feature something to find. It mentions a lighthouse, a quiet harbour, seventeen orange kayaks and a recipe for lemon cake, so every page has a few unusual words.",
        "When a file is very long, small details matter. Thumbnails must appear without delay, scrolling must stay smooth, and memory use must stay flat even after hours of reading.",
        "Forms arrive in every shape: tax returns, school trips, insurance claims and gym memberships. Some have real fields, others are just lines printed on a page, waiting for a pen.",
        "Privacy is not a feature to be added later. Files stay on this computer, nothing is uploaded, and when something is redacted it is removed from the file rather than hidden under a black box.",
    ];

    /// <summary>Returns <paramref name="count"/> paragraphs, rotating through the list starting at <paramref name="start"/>.</summary>
    public static IEnumerable<string> Take(int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return Paragraphs[(start + i) % Paragraphs.Length];
        }
    }
}
