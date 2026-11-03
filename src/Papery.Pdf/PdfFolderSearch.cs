using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Papery.Pdf;

/// <summary>One place a search found its words: the file, the page, and a little of the text around it.</summary>
public sealed record FolderSearchHit(string Path, int PageIndex, string Before, string Match, string After);

/// <summary>How far a folder search has got. Locked files can't be read without their password, so they're counted, not searched.</summary>
public sealed record FolderSearchProgress(int FilesDone, int FilesTotal, int Locked);

/// <summary>
/// Searches the words of every PDF in a folder and its subfolders, one file at a time, so results arrive while it works.
/// Line breaks in a PDF fall wherever the page ran out, so the words match across them.
/// </summary>
internal static partial class PdfFolderSearch
{
    // Enough for a big shared drive, without letting a search of C:\ run for an hour.
    private const int MostFiles = 10_000;
    private const int MostHitsPerFile = 50;
    private const int Around = 48;

    public static async IAsyncEnumerable<FolderSearchHit> SearchAsync(
        PdfEngine engine,
        string folder,
        string query,
        IProgress<FolderSearchProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !Directory.Exists(folder))
        {
            yield break;
        }

        var pattern = new Regex(string.Join(@"\s+", words.Select(Regex.Escape)), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        string[] files = Directory.EnumerateFiles(folder, "*.pdf", options).Take(MostFiles).ToArray();
        int locked = 0;
        for (int i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new FolderSearchProgress(i, files.Length, locked));
            var (hits, wasLocked) = await SearchFileAsync(engine, files[i], pattern, cancellationToken).ConfigureAwait(false);
            locked += wasLocked ? 1 : 0;
            foreach (var hit in hits)
            {
                yield return hit;
            }
        }

        progress?.Report(new FolderSearchProgress(files.Length, files.Length, locked));
    }

    private static async Task<(List<FolderSearchHit> Hits, bool Locked)> SearchFileAsync(PdfEngine engine, string path, Regex pattern, CancellationToken cancellationToken)
    {
        var hits = new List<FolderSearchHit>();
        PdfDocument document;
        try
        {
            document = await engine.OpenAsync(path, null, cancellationToken).ConfigureAwait(false);
        }
        catch (PdfOpenException ex)
        {
            // A damaged or unreadable file is skipped; one bad file shouldn't end the search.
            return (hits, ex.Error == PdfOpenError.PasswordRequired);
        }

        await using (document.ConfigureAwait(false))
        {
            for (int page = 0; page < document.PageCount && hits.Count < MostHitsPerFile; page++)
            {
                string text = await document.GetPageTextAsync(page, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
                foreach (Match match in pattern.Matches(text))
                {
                    int from = Math.Max(0, match.Index - Around), to = Math.Min(text.Length, match.Index + match.Length + Around);
                    hits.Add(new FolderSearchHit(path, page, Tidy(text[from..match.Index]), Tidy(match.Value), Tidy(text[(match.Index + match.Length)..to])));
                    if (hits.Count >= MostHitsPerFile)
                    {
                        break;
                    }
                }
            }
        }

        return (hits, false);
    }

    /// <summary>Line breaks and runs of spaces become single spaces, so a snippet reads as one line.</summary>
    private static string Tidy(string text) => Whitespace().Replace(text, " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
