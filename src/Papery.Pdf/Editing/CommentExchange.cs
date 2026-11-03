using System.Xml;
using System.Xml.Linq;

namespace Papery.Pdf.Editing;

/// <summary>What reading comments in did.</summary>
/// <param name="Added">Comments now in the PDF.</param>
/// <param name="Skipped">Kinds Papery has no tool for (attachments, sounds, review ticks), damaged ones, or ones for pages the PDF doesn't have.</param>
/// <param name="AlreadyThere">Comments the PDF already had (same name), so reading a file twice doesn't double them.</param>
public sealed record CommentImportResult(int Added, int Skipped, int AlreadyThere);

/// <summary>A rectangle in PDF user space (points, y up).</summary>
internal readonly record struct UserBox(double Left, double Bottom, double Right, double Top)
{
    public static UserBox Around(IEnumerable<UserPoint> points)
    {
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        foreach (var p in points)
        {
            (left, bottom, right, top) = (Math.Min(left, p.X), Math.Min(bottom, p.Y), Math.Max(right, p.X), Math.Max(top, p.Y));
        }

        return new UserBox(left, bottom, right, top);
    }

    public UserBox Grown(double by) => new(Left - by, Bottom - by, Right + by, Top + by);

    public UserBox Union(UserBox other) => new(Math.Min(Left, other.Left), Math.Min(Bottom, other.Bottom), Math.Max(Right, other.Right), Math.Max(Top, other.Top));

    public bool IsValid => Right >= Left && Top >= Bottom;
}

/// <summary>
/// One comment on its way between a PDF and an XFDF file, in PDF user space, which is what XFDF uses.
/// <see cref="Rect"/> is the annotation's /Rect; <see cref="Boxes"/> are the areas Papery draws in (a text mark's
/// lines, a shape's outline, a text box) and <see cref="Paths"/> its points (ink, a line's ends, a polygon's corners,
/// a callout's leader from box to tip).
/// </summary>
internal sealed record ExchangedComment(
    int PageIndex,
    PdfAnnotationKind Kind,
    UserBox Rect,
    IReadOnlyList<UserBox> Boxes,
    IReadOnlyList<IReadOnlyList<UserPoint>> Paths,
    PdfColor Color,
    float Width,
    float FontSize,
    string? Text,
    string? Author,
    string? Subject,
    string? Name,
    string? Modified,
    string? Created);

/// <summary>
/// Comments in and out of XFDF, Adobe's XML for swapping comments, so reviewers on Acrobat and Papery can work on one
/// file. Papery's own extras (a marker rather than a pen, a cloud rather than a box) ride in the subject, which Acrobat
/// shows as the comment's type anyway.
/// </summary>
internal static class CommentExchange
{
    /// <summary>More than any real review; past this the rest of a file is counted as skipped rather than read.</summary>
    public const int MostComments = 10_000;

    private const int MostPoints = 20_000;
    private const int MostPaths = 2_000;

    // How strong Papery's own highlighter is: an opaque highlight would hide the words under it.
    private const byte SeeThrough = 110;

    // ================================================================= writing

    public static async Task<int> ExportAsync(PdfDocument document, string xfdfPath, CancellationToken cancellationToken)
    {
        var comments = (await document.ReadCommentsForExchangeAsync(cancellationToken).ConfigureAwait(false)).Where(c => ElementFor(c) != null).ToList();
        if (comments.Count == 0)
        {
            return 0;
        }

        string pdfName = Path.GetFileName(document.FilePath);
        await Task.Run(() => Xfdf.WriteSafely(xfdfPath, stream => Write(stream, comments, pdfName)), cancellationToken).ConfigureAwait(false);
        return comments.Count;
    }

    internal static void Write(Stream stream, IReadOnlyList<ExchangedComment> comments, string pdfName)
    {
        using var xml = XmlWriter.Create(stream, Xfdf.WriterSettings);
        xml.WriteStartDocument();
        xml.WriteStartElement("xfdf", Xfdf.Namespace);
        xml.WriteAttributeString("xml", "space", null, "preserve");
        xml.WriteStartElement("annots", Xfdf.Namespace);
        foreach (var comment in comments)
        {
            if (ElementFor(comment) is { } element)
            {
                WriteOne(xml, element, comment);
            }
        }

        xml.WriteEndElement();
        xml.WriteStartElement("f", Xfdf.Namespace);
        xml.WriteAttributeString("href", Xfdf.Clean(pdfName));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    /// <summary>The XFDF element for a kind, or null for ones that aren't comments (pictures, cover-ups, signatures).</summary>
    private static string? ElementFor(ExchangedComment c) => c.Kind switch
    {
        PdfAnnotationKind.Note => "text",
        PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout => c.Boxes.Count > 0 ? "freetext" : null,
        PdfAnnotationKind.Highlight => "highlight",
        PdfAnnotationKind.Underline => "underline",
        PdfAnnotationKind.StrikeOut => "strikeout",
        PdfAnnotationKind.Squiggly => "squiggly",
        PdfAnnotationKind.Rectangle or PdfAnnotationKind.Cloud => "square",
        PdfAnnotationKind.Ellipse => "circle",
        PdfAnnotationKind.Line or PdfAnnotationKind.Arrow or PdfAnnotationKind.Measure => c.Paths is [{ Count: >= 2 }, ..] ? "line" : null,
        PdfAnnotationKind.Polygon => c.Paths is [{ Count: >= 3 }, ..] ? "polygon" : null,
        PdfAnnotationKind.Pen or PdfAnnotationKind.Marker => c.Paths.Count > 0 ? "ink" : null,
        PdfAnnotationKind.Stamp => "stamp",
        _ => null,
    };

    private static void WriteOne(XmlWriter xml, string element, ExchangedComment c)
    {
        xml.WriteStartElement(element, Xfdf.Namespace);
        xml.WriteAttributeString("page", c.PageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        xml.WriteAttributeString("rect", Box(c.Rect));
        xml.WriteAttributeString("color", Xfdf.Colour(c.Color));
        if (c.Color.A < 255)
        {
            xml.WriteAttributeString("opacity", Xfdf.Number(c.Color.A / 255.0));
        }

        Optional(xml, "name", c.Name);
        Optional(xml, "title", c.Author);
        Optional(xml, "subject", c.Subject ?? c.Kind.ToString());
        Optional(xml, "date", c.Modified);
        Optional(xml, "creationdate", c.Created);
        xml.WriteAttributeString("flags", "print");
        if (element is not ("text" or "highlight" or "underline" or "strikeout" or "squiggly" or "stamp" or "freetext"))
        {
            xml.WriteAttributeString("width", Xfdf.Number(c.Width));
        }

        switch (element)
        {
            case "text":
                xml.WriteAttributeString("icon", "Comment");
                break;
            case "highlight" or "underline" or "strikeout" or "squiggly":
                xml.WriteAttributeString("coords", string.Join(",", (c.Boxes.Count > 0 ? c.Boxes : [c.Rect]).Select(Quad)));
                break;
            case "square" when c.Kind == PdfAnnotationKind.Cloud:
                xml.WriteAttributeString("style", "cloudy");
                xml.WriteAttributeString("intensity", "1");
                break;
            case "line":
                var line = c.Paths[0];
                xml.WriteAttributeString("start", Point(line[0]));
                xml.WriteAttributeString("end", Point(line[^1]));
                if (c.Kind == PdfAnnotationKind.Arrow)
                {
                    xml.WriteAttributeString("tail", "OpenArrow"); // tail is the end point, where Papery draws the head
                }

                break;
            case "freetext":
                var box = c.Boxes[0];
                if (c.Kind == PdfAnnotationKind.Callout && c.Paths is [{ Count: >= 2 } leader, ..])
                {
                    // Acrobat lists the callout from the tip it points at back to the box.
                    xml.WriteAttributeString("intent", "FreeTextCallout");
                    xml.WriteAttributeString("callout", $"{Point(leader[^1])},{Point(leader[0])}");
                }

                if (box != c.Rect)
                {
                    xml.WriteAttributeString("fringe", Box(new UserBox(box.Left - c.Rect.Left, box.Bottom - c.Rect.Bottom, c.Rect.Right - box.Right, c.Rect.Top - box.Top)));
                }

                break;
        }

        if (!string.IsNullOrEmpty(c.Text))
        {
            xml.WriteElementString("contents", Xfdf.Namespace, Xfdf.Clean(c.Text));
        }

        switch (element)
        {
            case "freetext":
                xml.WriteElementString("defaultappearance", Xfdf.Namespace, Xfdf.Appearance(c.Color, c.FontSize));
                break;
            case "polygon":
                var corners = c.Paths[0];
                if (corners.Count > 3 && corners[^1] == corners[0])
                {
                    corners = corners.Take(corners.Count - 1).ToArray(); // Papery closes the loop itself
                }

                xml.WriteElementString("vertices", Xfdf.Namespace, string.Join(";", corners.Select(Point)));
                break;
            case "ink":
                xml.WriteStartElement("inklist", Xfdf.Namespace);
                foreach (var stroke in c.Paths.Where(p => p.Count > 0))
                {
                    xml.WriteElementString("gesture", Xfdf.Namespace, string.Join(";", stroke.Select(Point)));
                }

                xml.WriteEndElement();
                break;
        }

        xml.WriteEndElement();
    }

    private static void Optional(XmlWriter xml, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            xml.WriteAttributeString(name, Xfdf.Clean(value));
        }
    }

    private static string Point(UserPoint p) => $"{Xfdf.Number(p.X)},{Xfdf.Number(p.Y)}";

    private static string Box(UserBox b) => $"{Xfdf.Number(b.Left)},{Xfdf.Number(b.Bottom)},{Xfdf.Number(b.Right)},{Xfdf.Number(b.Top)}";

    // Corners in the order PDF and XFDF both use: top left, top right, bottom left, bottom right.
    private static string Quad(UserBox b) =>
        string.Join(",", new[] { b.Left, b.Top, b.Right, b.Top, b.Left, b.Bottom, b.Right, b.Bottom }.Select(Xfdf.Number));

    // ================================================================= reading

    public static async Task<CommentImportResult> ImportAsync(PdfEngine engine, PdfInputFile input, string xfdfPath, string outputPath, CancellationToken cancellationToken)
    {
        var (comments, skipped) = await Task.Run(() => Read(xfdfPath), cancellationToken).ConfigureAwait(false);
        await using var document = await engine.OpenAsync(input.Path, input.Password, cancellationToken).ConfigureAwait(false);
        if (!document.CanChangeAnnotations)
        {
            throw new PdfEditException(PdfEditError.NotAllowed, input.Path);
        }

        var (added, unplaced, already) = await document.AddExchangedCommentsAsync(comments, cancellationToken).ConfigureAwait(false);
        if (added > 0)
        {
            await document.SaveAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }

        return new CommentImportResult(added, skipped + unplaced, already);
    }

    /// <summary>The comments in an XFDF file, and how many were left out. Throws InvalidDataException for files that aren't XFDF.</summary>
    internal static (List<ExchangedComment> Comments, int Skipped) Read(string path)
    {
        var comments = new List<ExchangedComment>();
        int skipped = 0;
        try
        {
            using var reader = XmlReader.Create(Xfdf.OpenChecked(path), Xfdf.ReaderSettings);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "xfdf")
            {
                throw new InvalidDataException("That file isn't an XFDF comments file.");
            }

            bool inAnnots = false;
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                {
                    inAnnots = reader.LocalName == "annots" && !reader.IsEmptyElement;
                    if (!inAnnots)
                    {
                        reader.Skip(); // fields, ids and the like
                        continue;
                    }
                }
                else if (inAnnots && reader.NodeType == XmlNodeType.Element && reader.Depth == 2)
                {
                    if (comments.Count >= MostComments)
                    {
                        skipped++;
                        reader.Skip();
                        continue;
                    }

                    var element = (XElement)XNode.ReadFrom(reader);
                    if (element.Name.LocalName == "popup")
                    {
                        continue; // the note window that goes with a comment; not a comment of its own
                    }

                    if (FromElement(element) is { } comment)
                    {
                        comments.Add(comment);
                    }
                    else
                    {
                        skipped++;
                    }

                    continue;
                }

                reader.Read();
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("That file isn't an XFDF comments file Papery can read.", ex);
        }

        return (comments, skipped);
    }

    /// <summary>One XFDF annotation as a comment Papery can draw, or null when it can't.</summary>
    internal static ExchangedComment? FromElement(XElement e)
    {
        string type = e.Name.LocalName;
        if (Attr(e, "state") != null || Attr(e, "statemodel") != null)
        {
            return null; // a review tick ("Accepted"), which has no look of its own
        }

        if (!int.TryParse(Attr(e, "page"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int page)
            || Xfdf.ParseNumbers(Attr(e, "rect"), 4) is not [var x1, var y1, var x2, var y2])
        {
            return null;
        }

        var rect = new UserBox(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
        string? subject = Short(Attr(e, "subject"));
        PdfAnnotationKind? named = Enum.TryParse<PdfAnnotationKind>(subject, ignoreCase: false, out var k) && Enum.IsDefined(k) ? k : null;
        float width = Xfdf.ParseNumber(Attr(e, "width")) is { } w && w is >= 0 and <= 100 ? (float)w : 1.5f;
        var colour = Xfdf.ParseColour(Attr(e, "color")) ?? new PdfColor(0, 0, 0);
        if (Xfdf.ParseNumber(Attr(e, "opacity")) is { } opacity && opacity is >= 0 and < 1)
        {
            colour = colour with { A = (byte)Math.Round(opacity * 255) };
        }

        string? text = Contents(e);
        float fontSize = 12;
        List<UserBox> boxes = [];
        List<IReadOnlyList<UserPoint>> paths = [];
        PdfAnnotationKind kind;
        switch (type)
        {
            case "text":
                kind = PdfAnnotationKind.Note;
                boxes.Add(rect);
                if (Xfdf.ParseColour(Attr(e, "color")) == null)
                {
                    colour = new PdfColor(255, 205, 40); // the usual note yellow
                }

                break;

            case "freetext":
            {
                var (size, ink) = Xfdf.ParseAppearance(Child(e, "defaultappearance")?.Value ?? Attr(e, "defaultappearance"));
                fontSize = size ?? fontSize;
                colour = ink ?? colour; // the words' colour; /C on a text box is its background
                var box = rect;
                if (Xfdf.ParseNumbers(Attr(e, "fringe"), 4) is [var l, var b, var r, var t] && l >= 0 && b >= 0 && r >= 0 && t >= 0)
                {
                    box = new UserBox(rect.Left + l, rect.Bottom + b, rect.Right - r, rect.Top - t);
                }

                if (!box.IsValid)
                {
                    box = rect;
                }

                boxes.Add(box);
                kind = PdfAnnotationKind.TextBox;
                if (Points(Attr(e, "callout")) is { Count: >= 2 } callout)
                {
                    kind = PdfAnnotationKind.Callout;
                    paths.Add([callout[^1], callout[0]]); // Papery's leader runs from the box to the tip
                }

                break;
            }

            case "highlight" or "underline" or "strikeout" or "squiggly":
                kind = type switch
                {
                    "highlight" => PdfAnnotationKind.Highlight,
                    "underline" => PdfAnnotationKind.Underline,
                    "strikeout" => PdfAnnotationKind.StrikeOut,
                    _ => PdfAnnotationKind.Squiggly,
                };
                if (Xfdf.ParseNumbers(Attr(e, "coords"), MostPoints) is { Count: >= 8 } coords)
                {
                    for (int i = 0; i + 8 <= coords.Count; i += 8)
                    {
                        boxes.Add(UserBox.Around(Enumerable.Range(0, 4).Select(n => new UserPoint(coords[i + (n * 2)], coords[i + (n * 2) + 1]))));
                    }
                }
                else
                {
                    boxes.Add(rect);
                }

                break;

            case "square" or "circle":
            {
                kind = type == "circle" ? PdfAnnotationKind.Ellipse
                    : named == PdfAnnotationKind.Cloud || Attr(e, "style") == "cloudy" ? PdfAnnotationKind.Cloud
                    : PdfAnnotationKind.Rectangle;

                // The outline sits inside the rectangle: half the line's width in, or by the fringe when given.
                var box = Xfdf.ParseNumbers(Attr(e, "fringe"), 4) is [var l, var b, var r, var t]
                    ? new UserBox(rect.Left + l, rect.Bottom + b, rect.Right - r, rect.Top - t)
                    : rect.Grown(-width / 2);
                boxes.Add(box.IsValid ? box : rect);
                break;
            }

            case "line":
            {
                if (Points($"{Attr(e, "start")};{Attr(e, "end")}") is not [var start, var end])
                {
                    return null;
                }

                bool Arrowed(string name) => Attr(e, name)?.Contains("Arrow", StringComparison.OrdinalIgnoreCase) == true;
                kind = named == PdfAnnotationKind.Measure && !string.IsNullOrEmpty(text) ? PdfAnnotationKind.Measure
                    : Arrowed("tail") || Arrowed("head") || named == PdfAnnotationKind.Arrow ? PdfAnnotationKind.Arrow
                    : PdfAnnotationKind.Line;

                // Papery draws the head at the end, so an arrow pointing back at the start is turned round.
                paths.Add(kind == PdfAnnotationKind.Arrow && Arrowed("head") && !Arrowed("tail") ? [end, start] : [start, end]);
                break;
            }

            case "polygon" or "polyline":
            {
                if (Points(Child(e, "vertices")?.Value) is not { Count: >= 2 } corners || (type == "polygon" && corners.Count < 3))
                {
                    return null;
                }

                kind = type == "polygon" ? PdfAnnotationKind.Polygon : PdfAnnotationKind.Pen;
                paths.Add(corners);
                break;
            }

            case "ink":
                kind = named == PdfAnnotationKind.Marker ? PdfAnnotationKind.Marker : PdfAnnotationKind.Pen;
                foreach (var gesture in Child(e, "inklist")?.Elements().Where(g => g.Name.LocalName == "gesture").Take(MostPaths) ?? [])
                {
                    if (Points(gesture.Value) is { Count: > 0 } stroke)
                    {
                        paths.Add(stroke);
                    }
                }

                if (paths.Count == 0)
                {
                    return null;
                }

                break;

            case "stamp":
                kind = PdfAnnotationKind.Stamp;
                boxes.Add(rect);

                // Acrobat's stamps are named pictures ("Approved"); Papery writes the name as the stamp's words.
                text = !string.IsNullOrWhiteSpace(text) ? text
                    : subject is { Length: > 0 } && subject != nameof(PdfAnnotationKind.Stamp) ? subject
                    : Short(Attr(e, "icon"))?.TrimStart('#') ?? "Stamp";
                break;

            default:
                return null; // carets, attachments, sounds, links, redaction marks
        }

        if (kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Marker && colour.A == 255)
        {
            colour = colour with { A = SeeThrough }; // Acrobat blends instead; Papery's highlights are see-through
        }

        return new ExchangedComment(
            page,
            kind,
            rect,
            boxes,
            paths,
            colour,
            width,
            fontSize,
            text,
            Short(Attr(e, "title")),
            subject,
            Name(Attr(e, "name")),
            Date(Attr(e, "date")),
            Date(Attr(e, "creationdate")));
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);

    /// <summary>The comment's words: plain contents, or the text of the rich version Acrobat sometimes writes alone.</summary>
    private static string? Contents(XElement e)
    {
        if (Child(e, "contents") is { } plain)
        {
            return Xfdf.Clean(plain.Value);
        }

        if (Child(e, "contents-richtext") is { } rich)
        {
            var paragraphs = rich.Descendants().Where(d => d.Name.LocalName == "p").Select(p => p.Value).ToList();
            return Xfdf.Clean(paragraphs.Count > 0 ? string.Join("\n", paragraphs) : rich.Value);
        }

        return null;
    }

    /// <summary>"x,y;x,y" (or plain comma-separated pairs) as points, or null when it isn't that.</summary>
    private static List<UserPoint>? Points(string? text)
    {
        if (Xfdf.ParseNumbers(text, MostPoints * 2) is not { Count: >= 2 } numbers || numbers.Count % 2 != 0)
        {
            return null;
        }

        var points = new List<UserPoint>(numbers.Count / 2);
        for (int i = 0; i < numbers.Count; i += 2)
        {
            points.Add(new UserPoint(numbers[i], numbers[i + 1]));
        }

        return points;
    }

    private static string? Short(string? text) => string.IsNullOrWhiteSpace(text) ? null : Xfdf.Clean(text.Length > 500 ? text[..500] : text).Trim();

    /// <summary>A name safe to keep as the annotation's /NM, or null to make a new one.</summary>
    private static string? Name(string? name) =>
        name is { Length: > 0 and <= 128 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '{' or '}') ? name : null;

    /// <summary>A PDF date ("D:20261103120000+05'30'"), or null when it doesn't look like one.</summary>
    private static string? Date(string? date) =>
        date is { Length: >= 6 and <= 40 } && date.All(c => char.IsAsciiDigit(c) || c is 'D' or ':' or '+' or '-' or 'Z' or '\'') ? date : null;
}
