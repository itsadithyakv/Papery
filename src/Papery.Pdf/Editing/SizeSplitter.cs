namespace Papery.Pdf.Editing;

/// <summary>Split by file size ("at most 10 MB each", for email limits).</summary>
public static class SizeSplitter
{
    /// <summary>
    /// Splits so every file stays under <paramref name="limit"/> bytes. A file's size depends on what
    /// its pages share (fonts, images), so each part is found by writing trial files to a temp
    /// folder: grow the page count by doubling until it's too big, then binary-search the cut.
    /// A single page that is already too big becomes a file on its own.
    /// </summary>
    /// <param name="written">Receives each finished file as it is saved (so a cancelled split still reports them).</param>
    public static async Task SplitAsync(
        PdfEngine engine,
        int pageCount,
        long limit,
        long fileSize,
        Func<IReadOnlyList<int>, PdfComposeRequest> request,
        Func<SplitPart, string> pathFor,
        List<string> written,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string trials = Directory.CreateTempSubdirectory("papery-split-").FullName;
        int estimate = (int)Math.Max(2, (fileSize / Math.Max(1, limit)) + 1); // only used to pad part numbers
        try
        {
            int start = 0;
            while (start < pageCount)
            {
                async Task<bool> FitsAsync(int last)
                {
                    string trial = Path.Combine(trials, $"{start}-{last}.pdf");
                    await engine.ComposeAsync(request(Pages(start, last)), trial, null, cancellationToken);
                    long size = new FileInfo(trial).Length;
                    File.Delete(trial);
                    return size <= limit;
                }

                int good = start, bad = -1;
                if (await FitsAsync(start))
                {
                    // Grow: 1, 2, 4, 8... pages until a trial is too big or the document ends.
                    int count = 2;
                    while (good < pageCount - 1)
                    {
                        int last = Math.Min(pageCount - 1, start + count - 1);
                        if (await FitsAsync(last))
                        {
                            good = last;
                            count *= 2;
                        }
                        else
                        {
                            bad = last;
                            break;
                        }
                    }

                    // Narrow down between the last size that fitted and the first that didn't.
                    while (bad > good + 1)
                    {
                        int middle = (good + bad) / 2;
                        if (await FitsAsync(middle))
                        {
                            good = middle;
                        }
                        else
                        {
                            bad = middle;
                        }
                    }
                }

                var part = new SplitPart(SplitPlanner.PartName(written.Count, Math.Max(estimate, written.Count + 1)), Pages(start, good));
                string path = pathFor(part);
                await engine.ComposeAsync(request(part.Pages), path, null, cancellationToken);
                written.Add(path);
                start = good + 1;
                progress?.Report((double)start / pageCount);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(trials, recursive: true);
            }
            catch (IOException)
            {
                // A leftover trial file in %TEMP% is harmless; Windows cleans it up.
            }
        }

        static int[] Pages(int first, int last) => Enumerable.Range(first, last - first + 1).ToArray();
    }
}
