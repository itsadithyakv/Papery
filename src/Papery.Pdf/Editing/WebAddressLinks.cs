namespace Papery.Pdf.Editing;

/// <summary>
/// Turns web and email addresses written in the text into real links, so they work in every PDF app, not only in
/// Papery (which spots them as it shows the page). Addresses that already have a link over them are left alone.
/// </summary>
public static class WebAddressLinks
{
    /// <summary>The links to add for one page, from what <see cref="PdfDocument.GetLinksAsync"/> found on it.</summary>
    public static IReadOnlyList<NewPdfLink> ToAdd(int pageIndex, IReadOnlyList<PdfLink> links)
    {
        var real = links.Where(l => !l.IsDetected).Select(l => l.Area).ToArray();
        return links
            .Where(l => l.IsDetected && !string.IsNullOrWhiteSpace(l.Uri) && !real.Any(r => Overlaps(r, l.Area)))
            .Select(l => new NewPdfLink(pageIndex, l.Area, l.Uri, null))
            .ToArray();
    }

    /// <summary>Every page's, in page order.</summary>
    public static async Task<IReadOnlyList<NewPdfLink>> FindAsync(PdfDocument document, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var found = new List<NewPdfLink>();
        for (int page = 0; page < document.PageCount; page++)
        {
            found.AddRange(ToAdd(page, await document.GetLinksAsync(page, cancellationToken).ConfigureAwait(false)));
            progress?.Report((page + 1.0) / document.PageCount);
        }

        return found;
    }

    private static bool Overlaps(NormalizedRect a, NormalizedRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
}
