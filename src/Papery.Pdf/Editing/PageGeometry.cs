namespace Papery.Pdf.Editing;

/// <summary>
/// Changes to a page's shape, described the way the user sees the page (after its rotation):
/// straighten, then crop, then put it on a paper size and/or add margins.
/// </summary>
/// <param name="Crop">The part of the page to keep, as fractions of the page as displayed. Null = all of it.</param>
/// <param name="Paper">New paper size as displayed (width × height in points). Null = keep the size.</param>
/// <param name="FitContent">With a new paper size: scale the content to fill it (true), or keep its real size and only shrink if it wouldn't fit (false).</param>
/// <param name="Margin">Space kept clear around the content, in points.</param>
/// <param name="StraightenDegrees">Turns the content clockwise by this many degrees (to fix a crooked scan).</param>
public sealed record PageGeometry(
    NormalizedRect? Crop = null,
    PdfPageSize? Paper = null,
    bool FitContent = true,
    float Margin = 0,
    float StraightenDegrees = 0)
{
    public static readonly NormalizedRect Whole = new(0, 0, 1, 1);

    public bool IsEmpty => Crop is null && Paper is null && Margin <= 0 && StraightenDegrees == 0;

    /// <summary>True when the page gets a new paper size or margins (its content is moved and scaled).</summary>
    public bool ChangesPaper => Paper is not null || Margin > 0;

    /// <summary>
    /// Where things end up, for previews: the resulting paper size, where the content sits on it
    /// (fractions of the paper) and which part of the original page is shown (fractions of the page).
    /// The engine does the same maths in PDF space, and the tests check the two agree.
    /// </summary>
    public GeometryLayout Layout(PdfPageSize displayed)
    {
        var crop = Clamp(Crop ?? Whole);
        double contentW = displayed.Width * crop.Width, contentH = displayed.Height * crop.Height;
        if (!ChangesPaper || contentW <= 0 || contentH <= 0)
        {
            return new GeometryLayout(new PdfPageSize((float)contentW, (float)contentH), Whole, crop);
        }

        double margin = Math.Max(0, Margin);
        double paperW = Paper?.Width ?? contentW + (2 * margin), paperH = Paper?.Height ?? contentH + (2 * margin);
        double scale = Scale(contentW, contentH, paperW, paperH, margin, FitContent);
        double w = contentW * scale, h = contentH * scale;
        var content = new NormalizedRect((paperW - w) / 2 / paperW, (paperH - h) / 2 / paperH, w / paperW, h / paperH);
        return new GeometryLayout(new PdfPageSize((float)paperW, (float)paperH), content, crop);
    }

    /// <summary>How much the content is scaled to sit on the paper inside the margins.</summary>
    internal static double Scale(double contentW, double contentH, double paperW, double paperH, double margin, bool fit)
    {
        double availableW = Math.Max(1, paperW - (2 * margin)), availableH = Math.Max(1, paperH - (2 * margin));
        double fitScale = Math.Min(availableW / contentW, availableH / contentH);
        return fit ? fitScale : Math.Min(1, fitScale);
    }

    internal static NormalizedRect Clamp(NormalizedRect r)
    {
        double x = Math.Clamp(r.X, 0, 1), y = Math.Clamp(r.Y, 0, 1);
        double right = Math.Clamp(r.Right, x, 1), bottom = Math.Clamp(r.Bottom, y, 1);
        return new NormalizedRect(x, y, Math.Max(0.001, right - x), Math.Max(0.001, bottom - y));
    }
}

/// <param name="Paper">The page size after the change, as displayed.</param>
/// <param name="Content">Where the kept part of the page sits on the new paper (fractions of the paper).</param>
/// <param name="Source">Which part of the original page is kept (fractions of the original page).</param>
public sealed record GeometryLayout(PdfPageSize Paper, NormalizedRect Content, NormalizedRect Source);

/// <summary>Common paper sizes in points (portrait).</summary>
public static class PaperSizes
{
    public static readonly PdfPageSize A3 = new(841.89f, 1190.55f);
    public static readonly PdfPageSize A4 = new(595.28f, 841.89f);
    public static readonly PdfPageSize A5 = new(419.53f, 595.28f);
    public static readonly PdfPageSize Letter = new(612, 792);
    public static readonly PdfPageSize Legal = new(612, 1008);

    public const float PointsPerMillimetre = 72f / 25.4f;

    /// <summary>The paper turned to match a page: landscape pages get landscape paper.</summary>
    public static PdfPageSize Oriented(PdfPageSize paper, PdfPageSize like) =>
        (like.Width > like.Height) == (paper.Width > paper.Height) ? paper : new PdfPageSize(paper.Height, paper.Width);
}
