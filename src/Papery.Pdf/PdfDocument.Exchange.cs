using Papery.Pdf.Editing;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>
/// The PDFium side of swapping comments and form answers with other apps (see <see cref="CommentExchange"/> and
/// <see cref="FormDataExchange"/>). Exchange files use PDF user space, so everything here converts through
/// PDFium, which knows each page's rotation and crop box.
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>The owner allows adding and changing comments (permission bit 6).</summary>
    public bool CanChangeAnnotations => (Permissions & (1 << 5)) != 0;

    /// <summary>Every comment on every page as an exchange file needs it. Papery's own are read from how it drew them.</summary>
    internal Task<IReadOnlyList<ExchangedComment>> ReadCommentsForExchangeAsync(CancellationToken cancellationToken) =>
        Run<IReadOnlyList<ExchangedComment>>(() =>
        {
            var result = new List<ExchangedComment>();
            for (int pageIndex = 0; pageIndex < PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = pageIndex;
                WithPage(pageIndex, page =>
                {
                    int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
                    for (int i = 0; i < count; i++)
                    {
                        nint annotation = PdfiumNative.FPDFPage_GetAnnot(page, i);
                        if (annotation == 0)
                        {
                            continue;
                        }

                        try
                        {
                            if (Exchanged(page, index, annotation) is { } comment)
                            {
                                result.Add(comment);
                            }
                        }
                        finally
                        {
                            PdfiumNative.FPDFPage_CloseAnnot(annotation);
                        }
                    }

                    return true;
                });
            }

            return result;
        }, cancellationToken, PdfWorkPriority.Background);

    /// <summary>
    /// Draws comments from an exchange file onto their pages. Ones whose name is already on the page are left alone,
    /// so the same file read twice doesn't double up. Nothing reaches the file until <see cref="SaveAsync"/>.
    /// </summary>
    internal Task<(int Added, int Unplaced, int AlreadyThere)> AddExchangedCommentsAsync(IReadOnlyList<ExchangedComment> comments, CancellationToken cancellationToken) =>
        Run(() =>
        {
            int added = 0, unplaced = 0, already = 0;
            foreach (var group in comments.GroupBy(c => c.PageIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((uint)group.Key >= (uint)PageCount)
                {
                    unplaced += group.Count();
                    continue;
                }

                WithPage(group.Key, page =>
                {
                    var names = NamesOn(page);
                    foreach (var comment in group)
                    {
                        if (comment.Name != null && names.Contains(comment.Name))
                        {
                            already++;
                            continue;
                        }

                        if (DraftFor(page, comment) is not { } draft)
                        {
                            unplaced++;
                            continue;
                        }

                        string name = comment.Name ?? Guid.NewGuid().ToString("N");
                        WriteAnnotation(page, draft, name);
                        _drafts[name] = draft;
                        names.Add(name);
                        KeepDates(page, name, comment);
                        added++;
                    }

                    return true;
                });
            }

            HasAnnotationChanges |= added > 0;
            return (added, unplaced, already);
        }, cancellationToken, PdfWorkPriority.Background);

    /// <summary>The "on" name of a check box or radio button: what form data files give as its answer.</summary>
    internal unsafe Task<string> GetFieldExportValueAsync(int pageIndex, int fieldIndex, CancellationToken cancellationToken)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithField(pageIndex, fieldIndex, (form, _, annotation) =>
            ReadWide((b, n) => PdfiumNative.FPDFAnnot_GetFormFieldExportValue(form, annotation, b, n))), cancellationToken, PdfWorkPriority.Background);
    }

    // ================================================================= out

    private unsafe ExchangedComment? Exchanged(nint page, int pageIndex, nint annotation)
    {
        if (Read(page, pageIndex, annotation) is not { } info)
        {
            return null; // links, form fields, pop-ups, hidden ones
        }

        int subtype = PdfiumNative.FPDFAnnot_GetSubtype(annotation);
        PdfiumNative.FS_RECTF r;
        _ = PdfiumNative.FPDFAnnot_GetRect(annotation, &r);
        var stored = new UserBox(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));
        var kind = info.Kind == PdfAnnotationKind.Other && subtype == PdfiumNative.FPDF_ANNOT_POLYLINE ? PdfAnnotationKind.Pen : info.Kind;
        var color = info.Color;
        UserBox rect = stored;
        UserBox[] boxes;
        IReadOnlyList<UserPoint>[] paths;
        float width, fontSize = 12;

        if (_drafts.TryGetValue(info.Name, out var draft))
        {
            // Papery's own: the shape as drawn, not the padded rectangle around it, so a round trip lands in the same place.
            kind = draft.Kind;
            width = draft.Width;
            fontSize = draft.FontSize;
            boxes = draft.Areas?.Select(a => Box(ToUserBox(page, a))).ToArray() ?? [];
            paths = draft.Strokes?.Select(s => (IReadOnlyList<UserPoint>)s.Select(p => ToUser(page, p)).ToArray()).Where(s => s.Count > 0).ToArray() ?? [];
            if (boxes.Length > 0)
            {
                var box = boxes[0];
                rect = kind switch
                {
                    PdfAnnotationKind.Rectangle or PdfAnnotationKind.Ellipse or PdfAnnotationKind.Cloud => box.Grown(width / 2),
                    PdfAnnotationKind.Note => new UserBox(box.Left, box.Top - NoteSize, box.Left + NoteSize, box.Top),
                    PdfAnnotationKind.Callout when paths.Length > 0 => box.Union(UserBox.Around(paths[0])),
                    PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Stamp => box,
                    _ => stored,
                };
                if (kind == PdfAnnotationKind.Note)
                {
                    boxes = [rect]; // where the icon is
                }
            }
        }
        else
        {
            float h = 0, v = 0, border = 1;
            width = PdfiumNative.FPDFAnnot_GetBorder(annotation, &h, &v, &border) != 0 && border >= 0 ? border : 1;
            boxes = kind switch
            {
                PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.StrikeOut or PdfAnnotationKind.Squiggly => Quads(annotation, stored),
                PdfAnnotationKind.Rectangle or PdfAnnotationKind.Ellipse when stored.Grown(-width / 2) is { IsValid: true } inner => [inner],
                _ => [stored],
            };
            paths = subtype switch
            {
                PdfiumNative.FPDF_ANNOT_INK => Ink(annotation),
                PdfiumNative.FPDF_ANNOT_LINE => Line(annotation),
                PdfiumNative.FPDF_ANNOT_POLYGON or PdfiumNative.FPDF_ANNOT_POLYLINE => Vertices(annotation),
                _ => [],
            };
            if (subtype == PdfiumNative.FPDF_ANNOT_FREETEXT)
            {
                var (size, ink) = Xfdf.ParseAppearance(GetString(annotation, "DA"));
                fontSize = size ?? fontSize;
                color = ink ?? color;
            }
        }

        return new ExchangedComment(
            pageIndex,
            kind,
            rect,
            boxes,
            paths,
            color,
            width,
            fontSize,
            info.Text,
            info.Author,
            GetString(annotation, "Subj"),
            info.Name,
            GetString(annotation, "M"),
            GetString(annotation, "CreationDate"));
    }

    private static UserBox Box((UserPoint BottomLeft, UserPoint TopRight) b) => new(b.BottomLeft.X, b.BottomLeft.Y, b.TopRight.X, b.TopRight.Y);

    private static unsafe UserBox[] Quads(nint annotation, UserBox fallback)
    {
        int count = (int)Math.Min(PdfiumNative.FPDFAnnot_CountAttachmentPoints(annotation), 10_000);
        var result = new List<UserBox>(count);
        for (int i = 0; i < count; i++)
        {
            PdfiumNative.FS_QUADPOINTSF q;
            if (PdfiumNative.FPDFAnnot_GetAttachmentPoints(annotation, (nuint)i, &q) != 0)
            {
                result.Add(UserBox.Around([new(q.X1, q.Y1), new(q.X2, q.Y2), new(q.X3, q.Y3), new(q.X4, q.Y4)]));
            }
        }

        return result.Count > 0 ? [.. result] : [fallback];
    }

    private static unsafe IReadOnlyList<UserPoint>[] Ink(nint annotation)
    {
        uint count = Math.Min(PdfiumNative.FPDFAnnot_GetInkListCount(annotation), 2_000);
        var result = new List<IReadOnlyList<UserPoint>>();
        for (uint i = 0; i < count; i++)
        {
            uint length = PdfiumNative.FPDFAnnot_GetInkListPath(annotation, i, null, 0);
            if (length is > 0 and <= 100_000)
            {
                var points = new PdfiumNative.FS_POINTF[length];
                fixed (PdfiumNative.FS_POINTF* p = points)
                {
                    _ = PdfiumNative.FPDFAnnot_GetInkListPath(annotation, i, p, length);
                }

                result.Add(points.Select(p => new UserPoint(p.X, p.Y)).ToArray());
            }
        }

        return [.. result];
    }

    private static unsafe IReadOnlyList<UserPoint>[] Line(nint annotation)
    {
        PdfiumNative.FS_POINTF start, end;
        return PdfiumNative.FPDFAnnot_GetLine(annotation, &start, &end) != 0 ? [[new(start.X, start.Y), new(end.X, end.Y)]] : [];
    }

    private static unsafe IReadOnlyList<UserPoint>[] Vertices(nint annotation)
    {
        uint length = PdfiumNative.FPDFAnnot_GetVertices(annotation, null, 0);
        if (length is 0 or > 100_000)
        {
            return [];
        }

        var points = new PdfiumNative.FS_POINTF[length];
        fixed (PdfiumNative.FS_POINTF* p = points)
        {
            _ = PdfiumNative.FPDFAnnot_GetVertices(annotation, p, length);
        }

        return [points.Select(p => new UserPoint(p.X, p.Y)).ToArray()];
    }

    // ================================================================= in

    private static HashSet<string> NamesOn(nint page)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        int count = PdfiumNative.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            nint annotation = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (annotation == 0)
            {
                continue;
            }

            try
            {
                if (GetString(annotation, "NM") is { } name)
                {
                    names.Add(name);
                }
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annotation);
            }
        }

        return names;
    }

    /// <summary>The comment as one of Papery's drafts, in fractions of the page as displayed, or null if it can't be drawn.</summary>
    private static PdfAnnotationDraft? DraftFor(nint page, ExchangedComment c)
    {
        var areas = c.Boxes.Where(b => b.IsValid).Select(b => Normalize(page, b.Left, b.Top, b.Right, b.Bottom)).ToArray();
        var strokes = c.Paths.Where(p => p.Count > 0).Select(p => (IReadOnlyList<PagePoint>)p.Select(q => ToPage(page, q)).ToArray()).ToArray();
        var kind = c.Kind;
        float width = c.Width > 0 ? c.Width : 1;
        switch (kind)
        {
            case PdfAnnotationKind.Line or PdfAnnotationKind.Arrow or PdfAnnotationKind.Measure:
                if (strokes is not [{ Count: >= 2 } line, ..])
                {
                    return null;
                }

                strokes = [[line[0], line[^1]]];
                break;
            case PdfAnnotationKind.Polygon when strokes is not [{ Count: >= 3 }, ..]:
            case PdfAnnotationKind.Pen or PdfAnnotationKind.Marker when strokes.Length == 0:
                return null;
            case PdfAnnotationKind.Callout when strokes is not [{ Count: >= 2 }, ..]:
                kind = PdfAnnotationKind.TextBox;
                break;
        }

        bool usesArea = kind is not (PdfAnnotationKind.Line or PdfAnnotationKind.Arrow or PdfAnnotationKind.Measure or PdfAnnotationKind.Polygon
            or PdfAnnotationKind.Pen or PdfAnnotationKind.Marker);
        if (usesArea && areas.Length == 0)
        {
            return null;
        }

        return new PdfAnnotationDraft(
            kind,
            c.Color,
            usesArea ? areas : null,
            kind is PdfAnnotationKind.TextBox ? null : strokes.Length > 0 ? strokes : null,
            width,
            c.Text,
            c.FontSize,
            c.Author);
    }

    /// <summary>A point in user space to fractions of the displayed page (the reverse of <see cref="ToUser"/>).</summary>
    private static unsafe PagePoint ToPage(nint page, UserPoint p)
    {
        int x, y;
        _ = PdfiumNative.FPDF_PageToDevice(page, 0, 0, DeviceScale, DeviceScale, 0, p.X, p.Y, &x, &y);
        return new PagePoint(x / (double)DeviceScale, y / (double)DeviceScale);
    }

    /// <summary>Keeps the reviewer's own dates rather than the moment of importing. The new annotation is the page's last.</summary>
    private static void KeepDates(nint page, string name, ExchangedComment c)
    {
        if (c.Modified == null && c.Created == null)
        {
            return;
        }

        nint annotation = PdfiumNative.FPDFPage_GetAnnot(page, PdfiumNative.FPDFPage_GetAnnotCount(page) - 1);
        if (annotation == 0)
        {
            return;
        }

        try
        {
            if (GetString(annotation, "NM") == name)
            {
                if (c.Modified != null)
                {
                    SetString(annotation, "M", c.Modified);
                }

                if (c.Created != null)
                {
                    SetString(annotation, "CreationDate", c.Created);
                }
            }
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annotation);
        }
    }
}
