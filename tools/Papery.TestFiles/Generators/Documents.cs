using static Papery.TestFiles.Layout;

namespace Papery.TestFiles.Generators;

/// <summary>Ordinary documents: the huge file, bookmarks/links, mixed page sizes, and the merge set.</summary>
internal static class Documents
{
    // Soft header colours so each merge-set file is easy to recognise at a glance.
    private static readonly XColor[] PartColours =
    [
        XColor.FromArgb(255, 205, 196), XColor.FromArgb(255, 230, 170), XColor.FromArgb(200, 235, 200),
        XColor.FromArgb(190, 220, 245), XColor.FromArgb(225, 205, 240),
    ];

    /// <summary>
    /// The performance file: <paramref name="pageCount"/> pages, each with real text and its own
    /// incompressible image, sized so the whole file is about <paramref name="targetMegabytes"/> MB.
    /// Every page has a unique marker word (PAPERY-MARKER-0001...) for search tests,
    /// and there is a bookmark every 50 pages for "split by bookmarks".
    /// </summary>
    public static async Task LongDocumentAsync(string path, int pageCount, int targetMegabytes, Action<string> progress)
    {
        // Calibrate the image size once so each page's image is roughly the right number of bytes.
        long targetPerPage = Math.Max(20_000, (targetMegabytes * 1024L * 1024L / pageCount) - 6_000);
        int side = 400;
        byte[] probe = await ImageEncoder.JpegAsync(side, side, Pictures.Noise(side, side, 0), 0.85f);
        side = (int)(side * Math.Sqrt((double)targetPerPage / probe.Length));

        var doc = new SharpDoc();
        doc.Info.Title = $"Papery long test document ({pageCount} pages)";
        doc.Info.Author = "Papery test generator";

        PdfOutline? chapter = null;
        for (int n = 1; n <= pageCount; n++)
        {
            var page = AddPage(doc, A4);
            using (var g = XGraphics.FromPdfPage(page))
            {
                int chapterNo = ((n - 1) / 50) + 1;
                double y = DrawTitle(g, page, $"Chapter {chapterNo}", $"Page {n} of {pageCount}");
                g.DrawString($"PAPERY-MARKER-{n:0000}", LabelFont, Coral, Margin, y, XStringFormats.TopLeft);
                y += 22;

                foreach (string paragraph in Content.Take(n, 2))
                {
                    y = DrawWrapped(g, paragraph, BodyFont, Ink, Margin, y, page.Width.Point - (2 * Margin)) + 8;
                }

                byte[] jpeg = await ImageEncoder.JpegAsync(side, side, Pictures.Noise(side, side, n), 0.85f);
                using var image = XImage.FromStream(new MemoryStream(jpeg));
                double imageSize = Math.Min(page.Width.Point - (2 * Margin), page.Height.Point - y - 80);
                g.DrawImage(image, Margin, y + 10, imageSize, imageSize);
                DrawFooter(g, page, $"Papery test file · page {n}");
            }

            if ((n - 1) % 50 == 0)
            {
                chapter = doc.Outlines.Add($"Chapter {((n - 1) / 50) + 1}", page, false);
            }
            else if ((n - 1) % 10 == 0 && chapter != null)
            {
                chapter.Outlines.Add($"Section starting page {n}", page, false);
            }

            if (n % 100 == 0)
            {
                progress($"  {n}/{pageCount} pages");
            }
        }

        doc.Save(path);
    }

    /// <summary>12 pages with a 3-level outline, a contents page with internal links, and a web link.</summary>
    public static void BookmarksAndLinks(string path)
    {
        var doc = new SharpDoc();
        doc.Info.Title = "Papery bookmarks and links test";

        var contents = AddPage(doc, A4);
        var chapters = new[] { "Getting started", "Working with pages", "Sharing safely" };
        var pageNumbers = new List<(string Title, int Page)>();

        // Build chapter pages first so the contents page can link to them.
        int pageNo = 2;
        foreach (var (title, ci) in chapters.Select((t, i) => (t, i)))
        {
            var chapterPage = AddPage(doc, A4);
            DrawSimplePage(chapterPage, $"Chapter {ci + 1}: {title}", ci);
            var chapterOutline = doc.Outlines.Add($"Chapter {ci + 1}: {title}", chapterPage, true);
            pageNumbers.Add(($"Chapter {ci + 1}: {title}", pageNo++));

            for (int s = 1; s <= 2; s++)
            {
                var sectionPage = AddPage(doc, A4);
                DrawSimplePage(sectionPage, $"Section {ci + 1}.{s}", ci + s);
                var sectionOutline = chapterOutline.Outlines.Add($"Section {ci + 1}.{s}", sectionPage, false);
                pageNo++;

                if (s == 1)
                {
                    var subPage = AddPage(doc, A4);
                    DrawSimplePage(subPage, $"Section {ci + 1}.{s}.1 (a nested bookmark)", ci + 2);
                    sectionOutline.Outlines.Add($"Section {ci + 1}.{s}.1", subPage, false);
                    pageNo++;
                }
            }
        }

        // Contents page with clickable entries and a web link.
        doc.Outlines.Insert(0, new PdfOutline("Contents", contents, false));
        using (var g = XGraphics.FromPdfPage(contents))
        {
            double y = DrawTitle(g, contents, "Contents", "Click an entry to jump to it.");
            foreach (var (title, target) in pageNumbers)
            {
                var rect = new XRect(Margin, y, contents.Width.Point - (2 * Margin), 20);
                g.DrawString(title, HeadingFont, Ink, rect.X, rect.Y, XStringFormats.TopLeft);
                g.DrawString($"page {target}", LabelFont, Muted, new XRect(rect.X, rect.Y + 3, rect.Width, 16), XStringFormats.TopRight);
                contents.AddDocumentLink(new PdfRectangle(g.Transformer.WorldToDefaultPage(rect)), target);
                y += 30;
            }

            y += 20;
            var web = new XRect(Margin, y, 260, 16);
            g.DrawString("Web link: https://example.com", LabelFont, Coral, web.X, web.Y, XStringFormats.TopLeft);
            contents.AddWebLink(new PdfRectangle(g.Transformer.WorldToDefaultPage(web)), "https://example.com");
        }

        // A "back to contents" link at the bottom of every other page.
        for (int i = 1; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            using var g = XGraphics.FromPdfPage(page);
            var rect = new XRect(Margin, page.Height.Point - 60, 140, 14);
            g.DrawString("← Back to contents", LabelFont, Coral, rect.X, rect.Y, XStringFormats.TopLeft);
            page.AddDocumentLink(new PdfRectangle(g.Transformer.WorldToDefaultPage(rect)), 1);
        }

        doc.Save(path);
    }

    /// <summary>Pages of many sizes, some with /Rotate set, each labelled with what it should look like.</summary>
    public static void MixedSizesAndRotations(string path)
    {
        var specs = new (string Name, XSize Size, int Rotate)[]
        {
            ("A4 portrait, no rotation", A4, 0),
            ("US Letter, rotated 90°", Letter, 90),
            ("A3 landscape, no rotation", new XSize(A3.Height, A3.Width), 0),
            ("A5, rotated 180°", A5, 180),
            ("US Legal, rotated 270°", Legal, 270),
            ("Business card 3.5×2 in", new XSize(252, 144), 0),
            ("Poster 70×100 cm", new XSize(1984.25, 2834.65), 0),
            ("A4 landscape, rotated 90°", new XSize(A4.Height, A4.Width), 90),
        };

        var doc = new SharpDoc();
        doc.Info.Title = "Papery mixed sizes and rotations test";
        foreach (var (name, size, rotate) in specs)
        {
            var page = AddPage(doc, size);
            using (var g = XGraphics.FromPdfPage(page))
            {
                double w = page.Width.Point, h = page.Height.Point;
                g.DrawRectangle(new XPen(XColor.FromArgb(229, 83, 61), 3), 6, 6, w - 12, h - 12);
                double fontSize = Math.Clamp(Math.Min(w, h) / 14, 9, 60);
                var font = new XFont("Arial", fontSize, XFontStyleEx.Bold);
                g.DrawString(name, font, Ink, new XRect(0, (h / 2) - fontSize, w, fontSize * 1.4), XStringFormats.Center);
                g.DrawString(Inv($"{w:0} × {h:0} pt, /Rotate {rotate}"), new XFont("Arial", fontSize * 0.5), Muted,
                    new XRect(0, (h / 2) + (fontSize * 0.6), w, fontSize), XStringFormats.Center);
                g.DrawString("▲ TOP OF CONTENT", new XFont("Arial", fontSize * 0.5, XFontStyleEx.Bold), Coral,
                    new XRect(0, 12, w, fontSize), XStringFormats.Center);
            }

            // Set /Rotate after drawing so PDFsharp doesn't try to compensate for it.
            if (rotate != 0)
            {
                page.Elements.SetInteger("/Rotate", rotate);
            }
        }

        doc.Save(path);
    }

    /// <summary>20 small numbered PDFs of varying length and size, for the "merge 20 files" test.</summary>
    public static void MergeSet(string folder)
    {
        Directory.CreateDirectory(folder);
        for (int part = 1; part <= 20; part++)
        {
            var doc = new SharpDoc();
            doc.Info.Title = $"Merge set part {part:00}";
            int pages = (part % 5) + 1;
            XSize size = part % 7 == 0 ? new XSize(A4.Height, A4.Width) : part % 2 == 0 ? Letter : A4;
            for (int p = 1; p <= pages; p++)
            {
                var page = AddPage(doc, size);
                using var g = XGraphics.FromPdfPage(page);
                var colour = PartColours[part % PartColours.Length];
                g.DrawRectangle(new XSolidBrush(colour), 0, 0, page.Width.Point, 120);
                g.DrawString($"Part {part:00}", new XFont("Arial", 48, XFontStyleEx.Bold), Ink, Margin, 34, XStringFormats.TopLeft);
                g.DrawString($"page {p} of {pages}", HeadingFont, Ink, Margin, 140, XStringFormats.TopLeft);
                DrawWrapped(g, Content.Paragraphs[part % Content.Paragraphs.Length], BodyFont, Ink, Margin, 180, page.Width.Point - (2 * Margin));
            }

            doc.Save(Path.Combine(folder, $"part-{part:00}.pdf"));
        }
    }

    private static void DrawSimplePage(PdfPage page, string title, int seed)
    {
        using var g = XGraphics.FromPdfPage(page);
        double y = DrawTitle(g, page, title);
        foreach (string paragraph in Content.Take(seed, 3))
        {
            y = DrawWrapped(g, paragraph, BodyFont, Ink, Margin, y, page.Width.Point - (2 * Margin)) + 8;
        }
    }
}
