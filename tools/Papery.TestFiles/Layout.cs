using System.Text;

namespace Papery.TestFiles;

/// <summary>Page sizes, fonts and text helpers shared by all generators.</summary>
internal static class Layout
{
    // Page sizes in points (1 pt = 1/72 inch).
    public static readonly XSize A3 = new(841.89, 1190.55);
    public static readonly XSize A4 = new(595.28, 841.89);
    public static readonly XSize A5 = new(419.53, 595.28);
    public static readonly XSize Letter = new(612, 792);
    public static readonly XSize Legal = new(612, 1008);

    public const double Margin = 56; // ~2 cm

    public static readonly XFont TitleFont = new("Arial", 20, XFontStyleEx.Bold);
    public static readonly XFont HeadingFont = new("Arial", 14, XFontStyleEx.Bold);
    public static readonly XFont BodyFont = new("Times New Roman", 11);
    public static readonly XFont LabelFont = new("Arial", 10);
    public static readonly XFont SmallFont = new("Arial", 8);

    public static readonly XBrush Ink = new XSolidBrush(XColor.FromArgb(33, 33, 33));
    public static readonly XBrush Muted = new XSolidBrush(XColor.FromArgb(110, 110, 110));
    public static readonly XBrush Coral = new XSolidBrush(XColor.FromArgb(229, 83, 61));
    public static readonly XPen Rule = new(XColor.FromArgb(150, 150, 150), 0.75);

    public static PdfPage AddPage(SharpDoc doc, XSize size)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(size.Width);
        page.Height = XUnit.FromPoint(size.Height);
        return page;
    }

    /// <summary>Draws word-wrapped text starting at (x, y) and returns the y below the last line.</summary>
    public static double DrawWrapped(XGraphics g, string text, XFont font, XBrush brush, double x, double y, double maxWidth)
    {
        double lineHeight = font.Size * 1.3;
        var line = new StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = line.Length == 0 ? word : $"{line} {word}";
            if (line.Length > 0 && g.MeasureString(candidate, font).Width > maxWidth)
            {
                g.DrawString(line.ToString(), font, brush, x, y, XStringFormats.TopLeft);
                y += lineHeight;
                line.Clear().Append(word);
            }
            else
            {
                line.Clear().Append(candidate);
            }
        }

        if (line.Length > 0)
        {
            g.DrawString(line.ToString(), font, brush, x, y, XStringFormats.TopLeft);
            y += lineHeight;
        }

        return y;
    }

    /// <summary>Draws a page title, an optional subtitle and a coral rule. Returns the y where body text starts.</summary>
    public static double DrawTitle(XGraphics g, PdfPage page, string title, string? subtitle = null)
    {
        double y = Margin;
        g.DrawString(title, TitleFont, Ink, Margin, y, XStringFormats.TopLeft);
        y += 30;
        if (subtitle != null)
        {
            g.DrawString(subtitle, LabelFont, Muted, Margin, y, XStringFormats.TopLeft);
            y += 18;
        }

        g.DrawRectangle(Coral, Margin, y, page.Width.Point - (2 * Margin), 2);
        return y + 16;
    }

    public static void DrawFooter(XGraphics g, PdfPage page, string text) =>
        g.DrawString(text, SmallFont, Muted, new XRect(0, page.Height.Point - 36, page.Width.Point, 12), XStringFormats.Center);

    /// <summary>Formats numbers with a dot decimal separator, whatever the PC's region settings.</summary>
    public static string Inv(FormattableString s) => FormattableString.Invariant(s);
}
