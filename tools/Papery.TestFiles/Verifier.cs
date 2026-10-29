using System.Diagnostics;
using Papery.Pdf;
using Papery.TestFiles.Generators;

namespace Papery.TestFiles;

using PdfDocument = Papery.Pdf.PdfDocument;

/// <summary>
/// Opens every generated PDF with Papery's own engine and checks it behaves as intended:
/// the right page count, damaged files fail cleanly, passwords are required, and so on.
/// </summary>
internal static class Verifier
{
    private sealed record Expectation
    {
        public int? Pages { get; init; }
        public PdfOpenError? Error { get; init; }
        public string? Password { get; init; }
        public bool? Repaired { get; init; }
        public bool? Outline { get; init; }
        public bool? Encrypted { get; init; }
        public PdfFormType? Form { get; init; }
        public string[] TextContains { get; init; } = [];
        public string[] TextLacks { get; init; } = [];
        public (float W, float H)[]? Sizes { get; init; }
        public bool AnyOutcome { get; init; }
    }

    public static async Task<int> RunAsync(string root, int longPageCount)
    {
        var expected = BuildExpectations(longPageCount);
        int failures = 0;
        Console.WriteLine();
        Console.WriteLine("Verifying with Papery's PDFium engine:");

        foreach (string file in Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetRelativePath(root, file).Replace('\\', '/');
            var expect = expected.GetValueOrDefault(name) ?? new Expectation();
            var problems = new List<string>();
            string details;
            try
            {
                details = await CheckAsync(file, name, expect, problems);
            }
            catch (Exception ex)
            {
                problems.Add($"unexpected {ex.GetType().Name}: {ex.Message}");
                details = string.Empty;
            }

            bool ok = problems.Count == 0;
            failures += ok ? 0 : 1;
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  OK   " : "  FAIL ");
            Console.ResetColor();
            Console.WriteLine($"{name,-40} {details}");
            foreach (string problem in problems)
            {
                Console.WriteLine($"         -> {problem}");
            }
        }

        return failures;
    }

    private static async Task<string> CheckAsync(string file, string name, Expectation expect, List<string> problems)
    {
        double sizeMb = new FileInfo(file).Length / 1024.0 / 1024.0;
        var timer = Stopwatch.StartNew();
        PdfDocument doc;
        try
        {
            doc = await PdfEngine.Shared.OpenAsync(file);
        }
        catch (PdfOpenException ex)
        {
            if (expect.AnyOutcome)
            {
                return $"failed cleanly: {ex.Error}";
            }

            if (expect.Error != ex.Error)
            {
                problems.Add($"could not open: {ex.Error}");
                return string.Empty;
            }

            if (expect.Password == null)
            {
                return $"failed cleanly as expected: {ex.Error}";
            }

            // Wrong password must still fail; the right one must work.
            try
            {
                await (await PdfEngine.Shared.OpenAsync(file, "wrong-password")).DisposeAsync();
                problems.Add("opened with a WRONG password");
            }
            catch (PdfOpenException)
            {
            }

            timer.Restart();
            doc = await PdfEngine.Shared.OpenAsync(file, expect.Password);
        }

        await using (doc)
        {
            double openMs = timer.Elapsed.TotalMilliseconds;
            if (expect.Error != null && expect.Password == null && !expect.AnyOutcome)
            {
                problems.Add($"opened, but should have failed with {expect.Error}");
            }

            Check(problems, expect.Pages, doc.PageCount, "pages");
            Check(problems, expect.Repaired, doc.WasRepaired, "repaired");
            Check(problems, expect.Outline, doc.HasOutline, "has bookmarks");
            Check(problems, expect.Encrypted, doc.IsEncrypted, "encrypted");
            Check(problems, expect.Form, doc.FormType, "form type");

            if (expect.Sizes is { } sizes)
            {
                for (int i = 0; i < sizes.Length && i < doc.PageCount; i++)
                {
                    var actual = doc.PageSizes[i];
                    if (Math.Abs(actual.Width - sizes[i].W) > 1 || Math.Abs(actual.Height - sizes[i].H) > 1)
                    {
                        problems.Add($"page {i + 1} is {actual.Width:0}×{actual.Height:0}, expected {sizes[i].W:0}×{sizes[i].H:0}");
                    }
                }
            }

            if (expect.TextContains.Length > 0 || expect.TextLacks.Length > 0)
            {
                var all = new System.Text.StringBuilder();
                for (int i = 0; i < doc.PageCount; i++)
                {
                    all.Append(await doc.GetPageTextAsync(i)).Append('\n');
                }

                string text = all.ToString();
                problems.AddRange(expect.TextContains.Where(t => !text.Contains(t, StringComparison.Ordinal)).Select(t => $"text missing: '{t}'"));
                problems.AddRange(expect.TextLacks.Where(t => text.Contains(t, StringComparison.Ordinal)).Select(t => $"text should not be selectable: '{t}'"));
            }

            string extra = string.Empty;
            if (name.StartsWith("long-", StringComparison.Ordinal))
            {
                extra = await PerformanceProbeAsync(doc, problems);
            }

            if (doc.PageCount > 0)
            {
                // Rendering the first page proves the content is readable, not just the structure.
                var first = doc.PageSizes[0];
                await doc.RenderPageAsync(0, 400, Math.Max(1, (int)(400 * first.Height / first.Width)));
            }

            string flags = string.Join(", ", new[]
            {
                doc.IsEncrypted ? $"encrypted r{doc.SecurityRevision}" : null,
                doc.WasRepaired ? "repaired" : null,
                doc.HasOutline ? "bookmarks" : null,
                doc.FormType != PdfFormType.None ? doc.FormType.ToString() : null,
            }.Where(f => f != null));

            return Layout.Inv($"{doc.PageCount,5} pages {sizeMb,8:0.0} MB  opened in {openMs,6:0} ms  {flags} {extra}");
        }
    }

    private static async Task<string> PerformanceProbeAsync(PdfDocument doc, List<string> problems)
    {
        int middle = doc.PageCount / 2;
        var timer = Stopwatch.StartNew();
        await doc.RenderPageAsync(middle, 1240, 1754);
        double renderMs = timer.Elapsed.TotalMilliseconds;

        string text = await doc.GetPageTextAsync(middle);
        string marker = $"PAPERY-MARKER-{middle + 1:0000}";
        if (!text.Contains(marker, StringComparison.Ordinal))
        {
            problems.Add($"page {middle + 1} text does not contain {marker}");
        }

        return Layout.Inv($"| page {middle + 1} rendered at 150 DPI in {renderMs:0} ms");
    }

    private static void Check<T>(List<string> problems, T? expected, T actual, string what)
        where T : struct
    {
        if (expected is T e && !EqualityComparer<T>.Default.Equals(e, actual))
        {
            problems.Add($"{what}: expected {e}, got {actual}");
        }
    }

    private static Dictionary<string, Expectation> BuildExpectations(int longPageCount)
    {
        var passwordProtected = new Expectation { Error = PdfOpenError.PasswordRequired, Password = Security.UserPassword, Pages = 3, Encrypted = true };
        var expectations = new Dictionary<string, Expectation>(StringComparer.OrdinalIgnoreCase)
        {
            ["long-1000-pages.pdf"] = new() { Pages = longPageCount, Outline = true },
            ["scanned-document.pdf"] = new() { Pages = 5, TextLacks = ["Quarterly", "lighthouse"] },
            ["forms-interactive.pdf"] = new() { Pages = 2, Form = PdfFormType.AcroForm },
            ["forms-flat.pdf"] = new() { Pages = 1, Form = PdfFormType.None, TextContains = ["School trip consent form"] },
            ["password-aes256.pdf"] = passwordProtected,
            ["password-aes128.pdf"] = passwordProtected,
            ["password-rc4.pdf"] = passwordProtected,
            ["restricted-no-print-no-copy.pdf"] = new() { Pages = 3, Encrypted = true },
            ["damaged-broken-xref.pdf"] = new() { Pages = 5, Repaired = true },
            ["damaged-truncated.pdf"] = new() { AnyOutcome = true },
            ["damaged-not-a-pdf.pdf"] = new() { Error = PdfOpenError.Damaged },
            ["damaged-empty.pdf"] = new() { Error = PdfOpenError.Damaged },
            ["damaged-garbage.pdf"] = new() { Error = PdfOpenError.Damaged },
            ["bookmarks-and-links.pdf"] = new() { Pages = 13, Outline = true },
            ["hidden-data.pdf"] = new() { Pages = 1, TextContains = ["Visible text"] },
            ["redaction-test.pdf"] = new()
            {
                Pages = 2,
                TextContains = ["alice.smith@example.com", "SECRET-PHRASE-ALPHA", "SECRET-IN-XOBJECT", "dev.patel@example.co.uk"],
                TextLacks = ["SECRET-IN-IMAGE"],
            },
            ["mixed-sizes-and-rotations.pdf"] = new()
            {
                Pages = 8,
                // Display sizes, i.e. AFTER /Rotate is applied: 90° and 270° swap width and height.
                Sizes = [(595, 842), (792, 612), (1191, 842), (420, 595), (1008, 612), (252, 144), (1984, 2835), (595, 842)],
            },
        };

        for (int part = 1; part <= 20; part++)
        {
            expectations[$"merge-set/part-{part:00}.pdf"] = new() { Pages = (part % 5) + 1 };
        }

        return expectations;
    }
}
