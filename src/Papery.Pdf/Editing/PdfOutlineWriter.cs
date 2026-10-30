using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace Papery.Pdf.Editing;

/// <summary>
/// Adds bookmarks and a title to a freshly built PDF. PDFium can read bookmarks but not write
/// them, so this one step uses PDFsharp (pinned to 6.2.4, the last MIT-licensed version).
/// </summary>
internal static class PdfOutlineWriter
{
    // PDF viewers struggle with absurdly deep trees; real documents rarely go past 6.
    private const int MaxDepth = 32;

    public static void Write(string inputPath, string outputPath, IReadOnlyList<PdfOutlineItem>? outline, string? title, string? password = null)
    {
        using var doc = password is null
            ? PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify)
            : PdfReader.Open(inputPath, password, PdfDocumentOpenMode.Modify);
        if (!string.IsNullOrWhiteSpace(title))
        {
            doc.Info.Title = title;
        }

        if (outline != null)
        {
            // Replacing them all, so don't let PDFsharp read the old ones: it throws on some real-world
            // bookmarks it can't make sense of.
            doc.Internals.Catalog.Elements.Remove("/Outlines");
        }

        if (outline is { Count: > 0 })
        {
            Add(doc, doc.Outlines, outline, 0);
            doc.PageMode = PdfPageMode.UseOutlines; // open with the bookmarks showing
        }
        else if (outline != null)
        {
            // An empty list means "no bookmarks"; null leaves them alone.
            if (doc.PageMode == PdfPageMode.UseOutlines)
            {
                doc.PageMode = PdfPageMode.UseNone;
            }
        }

        // Open (not Create): the target is a hidden temp file, and Create refuses to overwrite hidden files.
        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16);
        stream.SetLength(0);
        doc.Save(stream, closeStream: false);
        stream.Flush(flushToDisk: true);
    }

    private static void Add(SharpDocument doc, PdfOutlineCollection into, IReadOnlyList<PdfOutlineItem> items, int depth)
    {
        foreach (var item in items)
        {
            // A bookmark without a target (or whose page was removed) takes its first child's page,
            // so the tree keeps its shape; with no target anywhere below, it is dropped.
            int? page = FirstPage(item);
            if (page is not int index || (uint)index >= (uint)doc.PageCount)
            {
                continue;
            }

            var added = into.Add(item.Title, doc.Pages[index], opened: depth == 0 && item.Children.Count > 0 && items.Count <= 1);
            if (item.Children.Count > 0 && depth < MaxDepth)
            {
                Add(doc, added.Outlines, item.Children, depth + 1);
            }
        }
    }

    private static int? FirstPage(PdfOutlineItem item)
    {
        if (item.PageIndex.HasValue)
        {
            return item.PageIndex;
        }

        foreach (var child in item.Children)
        {
            if (FirstPage(child) is int page)
            {
                return page;
            }
        }

        return null;
    }
}
