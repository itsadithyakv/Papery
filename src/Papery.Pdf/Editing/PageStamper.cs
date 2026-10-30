using System.Globalization;
using System.Text.RegularExpressions;
using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

public enum PageStampPosition
{
    TopLeft,
    TopCentre,
    TopRight,
    Middle,
    BottomLeft,
    BottomCentre,
    BottomRight,
}

/// <summary>
/// Something written on many pages: page numbers, a header or footer, a watermark or Bates numbers. In text,
/// <c>{page}</c> is the number (<c>{page:6}</c> pads it to six digits), <c>{pages}</c> the last number and <c>{date}</c> today.
/// </summary>
public sealed record PdfPageStamp
{
    public string? Text { get; init; }

    public PdfBitmap? Picture { get; init; }

    /// <summary>A picture's width on the page, in points.</summary>
    public double PictureWidth { get; init; } = 200;

    public PageStampPosition Position { get; init; } = PageStampPosition.BottomCentre;

    /// <summary>Distance from the page's edge, in points.</summary>
    public double Margin { get; init; } = 28;

    public PdfTextFont Font { get; init; }

    public bool Bold { get; init; }

    public bool Italic { get; init; }

    public float FontSize { get; init; } = 11;

    public PdfColor Color { get; init; } = new(60, 60, 60);

    /// <summary>0 (invisible) to 1 (solid).</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>Turned anticlockwise, so 45 rises from left to right.</summary>
    public double AngleDegrees { get; init; }

    /// <summary>Which pages, or null for all of them.</summary>
    public IReadOnlyList<int>? Pages { get; init; }

    public int StartNumber { get; init; } = 1;

    public bool SkipFirst { get; init; }
}

/// <summary>The sums behind page stamps, kept apart so they can be tested without any PDF.</summary>
public static partial class PageStampPlan
{
    /// <summary>Which pages get stamped and the number each shows. Pages left out don't use up a number.</summary>
    public static IReadOnlyList<(int Page, int Number)> Numbering(int pageCount, IReadOnlyList<int>? pages, bool skipFirst, int startNumber)
    {
        var chosen = (pages ?? Enumerable.Range(0, pageCount)).Where(p => (uint)p < (uint)pageCount).Distinct().Order().ToList();
        if (skipFirst && chosen.Count > 0)
        {
            chosen.RemoveAt(0);
        }

        return chosen.Select((page, i) => (page, startNumber + i)).ToArray();
    }

    /// <summary>Fills in a stamp's text for one page.</summary>
    public static string Resolve(string template, int number, int lastNumber, DateTime today)
    {
        string text = PageToken().Replace(template, match =>
        {
            int digits = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        });
        return text
            .Replace("{pages}", lastNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{date}", today.ToShortDateString(), StringComparison.Ordinal);
    }

    /// <summary>Where the middle of a stamp goes on a page, in points from the top-left as displayed.</summary>
    public static (double Across, double Down) Centre(PageStampPosition position, double pageWidth, double pageHeight, double stampWidth, double stampHeight, double margin)
    {
        double across = position switch
        {
            PageStampPosition.TopLeft or PageStampPosition.BottomLeft => margin + (stampWidth / 2),
            PageStampPosition.TopRight or PageStampPosition.BottomRight => pageWidth - margin - (stampWidth / 2),
            _ => pageWidth / 2,
        };
        double down = position switch
        {
            PageStampPosition.TopLeft or PageStampPosition.TopCentre or PageStampPosition.TopRight => margin + (stampHeight / 2),
            PageStampPosition.BottomLeft or PageStampPosition.BottomCentre or PageStampPosition.BottomRight => pageHeight - margin - (stampHeight / 2),
            _ => pageHeight / 2,
        };
        return (across, down);
    }

    [GeneratedRegex(@"\{page(?::(\d{1,2}))?\}")]
    private static partial Regex PageToken();
}

/// <summary>Writes a stamp into the pages' own content with PDFium, through the checked save.</summary>
internal static class PageStamper
{
    public static async Task ApplyAsync(PdfiumThread thread, PdfInputFile input, PdfPageStamp stamp, string outputPath, CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);
        RawPdf? doc = null;
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            int pages = await thread.InvokeAsync(() =>
            {
                doc = RawPdf.Load(input.Path, input.Password);
                Stamp(doc, stamp, input.Path, cancellationToken);
                return doc.PageCount;
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

            await thread.InvokeAsync(() => PdfComposer.Save(doc!, temp, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => PdfComposer.Verify(temp, input.Password, pages, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => { doc?.Dispose(); doc = null; }, PdfWorkPriority.Background, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
        }
        finally
        {
            await thread.InvokeAsync(() => doc?.Dispose(), PdfWorkPriority.Visible, CancellationToken.None).ConfigureAwait(false);
            SafeFile.TryDelete(temp);
        }
    }

    private static void Stamp(RawPdf doc, PdfPageStamp stamp, string path, CancellationToken cancellationToken)
    {
        var plan = PageStampPlan.Numbering(doc.PageCount, stamp.Pages, stamp.SkipFirst, stamp.StartNumber);
        int last = plan.Count > 0 ? plan[^1].Number : 0;
        var colour = stamp.Color with { A = (byte)Math.Clamp(stamp.Opacity * 255, 0, 255) };
        var picture = stamp.Picture is { } original ? Faded(original, stamp.Opacity) : null;
        var look = new TextLook(stamp.Font, stamp.Bold, stamp.Italic);
        foreach (var (index, number) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint page = PdfiumNative.FPDF_LoadPage(doc.Handle, index);
            if (page == 0)
            {
                throw new PdfEditException(PdfEditError.BuildFailed, path);
            }

            try
            {
                var builder = new AnnotationObjects(doc.Handle, page, 0, colour);
                var (width, height) = builder.PageSize();
                if (picture != null)
                {
                    double w = Math.Min(stamp.PictureWidth, width), h = w * picture.Height / picture.Width;
                    var (across, down) = PageStampPlan.Centre(stamp.Position, width, height, w, h, stamp.Margin);
                    builder.Picture(picture, new NormalizedRect((across - (w / 2)) / width, (down - (h / 2)) / height, w / width, h / height), (float)-stamp.AngleDegrees);
                }

                if (!string.IsNullOrEmpty(stamp.Text))
                {
                    string text = PageStampPlan.Resolve(stamp.Text, number, last, DateTime.Now);
                    double w = builder.TextWidth(text, stamp.FontSize, look);
                    var (across, down) = PageStampPlan.Centre(stamp.Position, width, height, w, stamp.FontSize, stamp.Margin);
                    builder.StampText(text, across, down, stamp.FontSize, stamp.AngleDegrees, look);
                }

                if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
                {
                    throw new PdfEditException(PdfEditError.BuildFailed, path);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>A copy of a picture made see-through by the stamp's opacity.</summary>
    private static PdfBitmap Faded(PdfBitmap picture, double opacity)
    {
        if (opacity >= 1)
        {
            return picture;
        }

        byte[] pixels = (byte[])picture.Pixels.Clone();
        for (int y = 0; y < picture.Height; y++)
        {
            int row = y * picture.Stride;
            for (int x = 0; x < picture.Width; x++)
            {
                int alpha = row + (x * 4) + 3;
                pixels[alpha] = (byte)(pixels[alpha] * opacity);
            }
        }

        return picture with { Pixels = pixels };
    }
}
