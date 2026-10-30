using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Papery.Pdf.Editing;
using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>
/// Annotations are added straight to this open document, so the page on screen is exactly what will
/// be saved. Nothing reaches the file until <see cref="SaveAsync"/>, which writes a checked copy and swaps it in.
/// </summary>
public sealed partial class PdfDocument
{
    private const int DeviceScale = 1_000_000;
    private const double NoteSize = 20;

    // Papery keeps how it drew an annotation inside the annotation (a private PDF key other apps
    // ignore), so its own annotations stay fully editable after the file is saved and reopened.
    private const string DraftKey = "PaperyDraft";

    // What Papery drew in this session, so it can be redrawn in another colour or place.
    private readonly Dictionary<string, PdfAnnotationDraft> _drafts = [];

    // Deleted but still undoable: hidden now, really removed when saving.
    private readonly HashSet<(int Page, string Name)> _hidden = [];

    /// <summary>True when annotations have changed since the file was opened or last saved.</summary>
    public bool HasAnnotationChanges { get; private set; }

    /// <summary>The annotations on a page that people work with (not links, form fields or pop-ups).</summary>
    public Task<IReadOnlyList<PdfAnnotationInfo>> GetAnnotationsAsync(int pageIndex, PdfWorkPriority priority = PdfWorkPriority.Visible, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run<IReadOnlyList<PdfAnnotationInfo>>(() => WithPage(pageIndex, page =>
        {
            var result = new List<PdfAnnotationInfo>();
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
                    if (Read(page, pageIndex, annotation) is { } info)
                    {
                        result.Add(info);
                    }
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annotation);
                }
            }

            return (IReadOnlyList<PdfAnnotationInfo>)result;
        }), cancellationToken, priority);
    }

    /// <summary>Adds an annotation. Pass <paramref name="name"/> to bring back one that was removed (redo).</summary>
    public Task<PdfAnnotationInfo> AddAnnotationAsync(int pageIndex, PdfAnnotationDraft draft, string? name = null, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithPage(pageIndex, page =>
        {
            string id = name ?? Guid.NewGuid().ToString("N");
            WriteAnnotation(page, draft, id);
            _drafts[id] = draft;
            HasAnnotationChanges = true;
            return Find(page, pageIndex, id)!;
        }), cancellationToken);
    }

    /// <summary>Hides (true) or brings back (false) an annotation. Deleting is hiding until saved, so undo is exact.</summary>
    public Task<bool> SetAnnotationHiddenAsync(int pageIndex, string name, bool hidden, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithPage(pageIndex, page => WithAnnotation(page, name, (annotation, _) =>
        {
            int flags = PdfiumNative.FPDFAnnot_GetFlags(annotation);
            _ = PdfiumNative.FPDFAnnot_SetFlags(annotation, hidden ? flags | PdfiumNative.FPDF_ANNOT_FLAG_HIDDEN : flags & ~PdfiumNative.FPDF_ANNOT_FLAG_HIDDEN);
            if (hidden)
            {
                _hidden.Add((pageIndex, name));
            }
            else
            {
                _hidden.Remove((pageIndex, name));
            }

            HasAnnotationChanges = true;
        })), cancellationToken);
    }

    /// <summary>
    /// Changes colour. Papery's own are redrawn; another app's lose their old look and each viewer redraws
    /// them. Returns false when that isn't possible (another app's ink or stamp).
    /// </summary>
    public Task<bool> RecolorAnnotationAsync(int pageIndex, string name, PdfColor color, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithPage(pageIndex, page =>
        {
            if (_drafts.TryGetValue(name, out var draft))
            {
                return Replace(page, name, draft with { Color = color });
            }

            bool changed = false;
            WithAnnotation(page, name, (annotation, _) =>
            {
                changed = PdfiumNative.FPDFAnnot_SetColor(annotation, PdfiumNative.FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, color.A) != 0;
                HasAnnotationChanges |= changed;
            });
            return changed;
        }), cancellationToken);
    }

    /// <summary>Changes the words of a note, text box or callout.</summary>
    public unsafe Task<bool> SetAnnotationTextAsync(int pageIndex, string name, string text, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithPage(pageIndex, page =>
        {
            if (_drafts.TryGetValue(name, out var draft) && draft.Kind is PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Stamp or PdfAnnotationKind.Measure)
            {
                return Replace(page, name, draft with { Text = text }); // the words are part of how it looks
            }

            if (draft != null)
            {
                _drafts[name] = draft with { Text = text };
            }

            return WithAnnotation(page, name, (annotation, _) =>
            {
                SetString(annotation, "Contents", text);
                HasAnnotationChanges = true;
            });
        }), cancellationToken);
    }

    /// <summary>Moves or resizes an annotation. Every viewer fits its appearance to this rectangle.</summary>
    public unsafe Task<bool> MoveAnnotationAsync(int pageIndex, string name, NormalizedRect bounds, CancellationToken cancellationToken = default)
    {
        CheckPageIndex(pageIndex);
        return Run(() => WithPage(pageIndex, page =>
        {
            if (_drafts.TryGetValue(name, out var draft) && Moved(draft, Find(page, pageIndex, name)?.Bounds, bounds) is { } moved)
            {
                return Replace(page, name, moved);
            }

            return WithAnnotation(page, name, (annotation, _) =>
            {
                var (bottomLeft, topRight) = ToUserBox(page, bounds);
                var rect = new PdfiumNative.FS_RECTF { Left = (float)bottomLeft.X, Bottom = (float)bottomLeft.Y, Right = (float)topRight.X, Top = (float)topRight.Y };
                _ = PdfiumNative.FPDFAnnot_SetRect(annotation, &rect);
                HasAnnotationChanges = true;
            });
        }), cancellationToken);
    }

    /// <summary>Writes the document with its annotations to a checked copy and swaps it into place.</summary>
    public async Task SaveAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath);
        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            await Run(() =>
            {
                foreach (var (pageIndex, name) in _hidden)
                {
                    WithPage(pageIndex, page => RemoveByName(page, name));
                }

                _hidden.Clear();
                PdfComposer.Save(_raw, temp, outputPath);
                return true;
            }, cancellationToken, PdfWorkPriority.Background).ConfigureAwait(false);
            await Run(() =>
            {
                PdfComposer.Verify(temp, Password, PageCount, outputPath);
                return true;
            }, cancellationToken, PdfWorkPriority.Background).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
            HasAnnotationChanges = false;
        }
        finally
        {
            SafeFile.TryDelete(temp);
        }
    }

    // ================================================================= reading

    private unsafe PdfAnnotationInfo? Read(nint page, int pageIndex, nint annotation)
    {
        int subtype = PdfiumNative.FPDFAnnot_GetSubtype(annotation);
        if (subtype is PdfiumNative.FPDF_ANNOT_LINK or PdfiumNative.FPDF_ANNOT_WIDGET or PdfiumNative.FPDF_ANNOT_POPUP
            || (PdfiumNative.FPDFAnnot_GetFlags(annotation) & PdfiumNative.FPDF_ANNOT_FLAG_HIDDEN) != 0)
        {
            return null;
        }

        string? name = GetString(annotation, "NM");
        if (string.IsNullOrEmpty(name))
        {
            // Another app's annotation with no name: give it one so it can be pointed at.
            name = Guid.NewGuid().ToString("N");
            SetString(annotation, "NM", name);
        }

        PdfiumNative.FS_RECTF rect;
        if (PdfiumNative.FPDFAnnot_GetRect(annotation, &rect) == 0)
        {
            return null;
        }

        if (!_drafts.ContainsKey(name) && GetString(annotation, DraftKey) is { } saved && Parse(saved) is { } restored)
        {
            _drafts[name] = restored;
        }

        var kind = KindOf(subtype, GetString(annotation, "Subj"));
        PdfColor color;
        if (_drafts.TryGetValue(name, out var draft))
        {
            color = draft.Color;
        }
        else
        {
            uint r = 0, g = 0, b = 0, a = 255;
            _ = PdfiumNative.FPDFAnnot_GetColor(annotation, PdfiumNative.FPDFANNOT_COLORTYPE_Color, &r, &g, &b, &a);
            color = new PdfColor((byte)r, (byte)g, (byte)b, (byte)a);
        }

        return new PdfAnnotationInfo(name, pageIndex, kind, color, Normalize(page, rect.Left, rect.Top, rect.Right, rect.Bottom), GetString(annotation, "Contents"), GetString(annotation, "T"));
    }

    private static PdfAnnotationKind KindOf(int subtype, string? subject)
    {
        // Lines, arrows and the like are stored as ink, stamps as stamps; the subject says which they are.
        if (subtype is PdfiumNative.FPDF_ANNOT_INK or PdfiumNative.FPDF_ANNOT_STAMP && Enum.TryParse<PdfAnnotationKind>(subject, out var named))
        {
            return named;
        }

        return subtype switch
        {
            PdfiumNative.FPDF_ANNOT_HIGHLIGHT => PdfAnnotationKind.Highlight,
            PdfiumNative.FPDF_ANNOT_UNDERLINE => PdfAnnotationKind.Underline,
            PdfiumNative.FPDF_ANNOT_SQUIGGLY => PdfAnnotationKind.Squiggly,
            PdfiumNative.FPDF_ANNOT_STRIKEOUT => PdfAnnotationKind.StrikeOut,
            PdfiumNative.FPDF_ANNOT_TEXT => PdfAnnotationKind.Note,
            PdfiumNative.FPDF_ANNOT_INK => PdfAnnotationKind.Pen,
            PdfiumNative.FPDF_ANNOT_SQUARE => PdfAnnotationKind.Rectangle,
            PdfiumNative.FPDF_ANNOT_CIRCLE => PdfAnnotationKind.Ellipse,
            PdfiumNative.FPDF_ANNOT_LINE => PdfAnnotationKind.Line,
            PdfiumNative.FPDF_ANNOT_POLYGON => PdfAnnotationKind.Polygon,
            PdfiumNative.FPDF_ANNOT_FREETEXT => PdfAnnotationKind.TextBox,
            PdfiumNative.FPDF_ANNOT_STAMP => PdfAnnotationKind.Stamp,
            _ => PdfAnnotationKind.Other,
        };
    }

    private PdfAnnotationInfo? Find(nint page, int pageIndex, string name)
    {
        PdfAnnotationInfo? found = null;
        WithAnnotation(page, name, (annotation, _) => found = Read(page, pageIndex, annotation));
        return found;
    }

    private static bool WithAnnotation(nint page, string name, Action<nint, int> work)
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
                if (GetString(annotation, "NM") == name)
                {
                    work(annotation, i);
                    return true;
                }
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annotation);
            }
        }

        return false;
    }

    private static bool RemoveByName(nint page, string name)
    {
        int index = -1;
        WithAnnotation(page, name, (_, i) => index = i);
        return index >= 0 && PdfiumNative.FPDFPage_RemoveAnnot(page, index) != 0;
    }

    private bool Replace(nint page, string name, PdfAnnotationDraft draft)
    {
        if (!RemoveByName(page, name))
        {
            return false;
        }

        WriteAnnotation(page, draft, name);
        _drafts[name] = draft;
        HasAnnotationChanges = true;
        return true;
    }

    /// <summary>The draft shifted and scaled so its bounds become <paramref name="to"/>. Text marks stay on their text.</summary>
    private static PdfAnnotationDraft? Moved(PdfAnnotationDraft draft, NormalizedRect? from, NormalizedRect to)
    {
        if (from is not { Width: > 0, Height: > 0 } f || draft.Kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.Squiggly or PdfAnnotationKind.StrikeOut)
        {
            return draft.Kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.Squiggly or PdfAnnotationKind.StrikeOut ? draft : null;
        }

        double sx = to.Width / f.Width, sy = to.Height / f.Height;
        return draft with
        {
            Areas = draft.Areas?.Select(a => new NormalizedRect(to.X + ((a.X - f.X) * sx), to.Y + ((a.Y - f.Y) * sy), a.Width * sx, a.Height * sy)).ToArray(),
            Strokes = draft.Strokes?.Select(s => (IReadOnlyList<PagePoint>)s.Select(p => p with { X = to.X + ((p.X - f.X) * sx), Y = to.Y + ((p.Y - f.Y) * sy) }).ToArray()).ToArray(),
        };
    }

    // ================================================================= writing

    private unsafe void WriteAnnotation(nint page, PdfAnnotationDraft draft, string name)
    {
        int subtype = draft.Kind switch
        {
            PdfAnnotationKind.Highlight => PdfiumNative.FPDF_ANNOT_HIGHLIGHT,
            PdfAnnotationKind.Underline => PdfiumNative.FPDF_ANNOT_UNDERLINE,
            PdfAnnotationKind.Squiggly => PdfiumNative.FPDF_ANNOT_SQUIGGLY,
            PdfAnnotationKind.StrikeOut => PdfiumNative.FPDF_ANNOT_STRIKEOUT,
            PdfAnnotationKind.Note => PdfiumNative.FPDF_ANNOT_TEXT,
            PdfAnnotationKind.Rectangle => PdfiumNative.FPDF_ANNOT_SQUARE,
            PdfAnnotationKind.Ellipse => PdfiumNative.FPDF_ANNOT_CIRCLE,
            PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Stamp or PdfAnnotationKind.Measure => PdfiumNative.FPDF_ANNOT_STAMP,
            _ => PdfiumNative.FPDF_ANNOT_INK, // PDFium can't create Line or Polygon; ink shows the same everywhere
        };
        nint annotation = PdfiumNative.FPDFPage_CreateAnnot(page, subtype);
        if (annotation == 0)
        {
            throw new InvalidOperationException($"Could not create a {draft.Kind} annotation.");
        }

        try
        {
            var c = draft.Color;
            _ = PdfiumNative.FPDFAnnot_SetColor(annotation, PdfiumNative.FPDFANNOT_COLORTYPE_Color, c.R, c.G, c.B, c.A);
            SetString(annotation, "NM", name);
            SetString(annotation, "Subj", draft.Kind.ToString());
            SetString(annotation, DraftKey, JsonSerializer.Serialize(draft, AnnotationJson.Default.PdfAnnotationDraft));
            SetString(annotation, "M", $"D:{DateTime.UtcNow:yyyyMMddHHmmss}Z");
            if (draft.Text != null)
            {
                SetString(annotation, "Contents", draft.Text);
            }

            if (draft.Author != null)
            {
                SetString(annotation, "T", draft.Author);
            }

            _ = PdfiumNative.FPDFAnnot_SetFlags(annotation, PdfiumNative.FPDF_ANNOT_FLAG_PRINT); // annotations print too
            if (subtype == PdfiumNative.FPDF_ANNOT_STAMP)
            {
                WriteObjects(page, annotation, draft);
            }
            else
            {
                WriteVector(page, annotation, draft);
            }
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annotation);
        }
    }

    private static unsafe void WriteVector(nint page, nint annotation, PdfAnnotationDraft draft)
    {
        var areas = draft.Areas ?? [];
        var strokes = (draft.Strokes ?? []).Select(s => (IReadOnlyList<UserPoint>)s.Select(p => ToUser(page, p)).ToArray()).Where(s => s.Count > 0).ToArray();
        string appearance;
        double pad = draft.Width + 1;
        List<UserPoint> extent = [];
        switch (draft.Kind)
        {
            case PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.Squiggly or PdfAnnotationKind.StrikeOut:
            {
                var lines = areas.Select(a => ToUserBox(page, a)).ToArray();
                foreach (var (bl, tr) in lines)
                {
                    var quad = new PdfiumNative.FS_QUADPOINTSF
                    {
                        X1 = (float)bl.X, Y1 = (float)tr.Y, X2 = (float)tr.X, Y2 = (float)tr.Y,
                        X3 = (float)bl.X, Y3 = (float)bl.Y, X4 = (float)tr.X, Y4 = (float)bl.Y,
                    };
                    _ = PdfiumNative.FPDFAnnot_AppendAttachmentPoints(annotation, &quad);
                    extent.Add(bl);
                    extent.Add(tr);
                }

                appearance = AnnotationAppearance.TextMark(draft.Kind, draft.Color, lines);
                pad = 1;
                break;
            }

            case PdfAnnotationKind.Note:
            {
                var (bl, tr) = ToUserBox(page, areas[0]);
                var topLeft = new UserPoint(bl.X, tr.Y);
                appearance = AnnotationAppearance.NoteIcon(draft.Color, topLeft, NoteSize);
                extent.Add(topLeft);
                extent.Add(new UserPoint(bl.X + NoteSize, tr.Y - NoteSize));
                break;
            }

            case PdfAnnotationKind.Rectangle or PdfAnnotationKind.Ellipse or PdfAnnotationKind.Cloud:
            {
                var (bl, tr) = ToUserBox(page, areas[0]);
                extent.Add(bl);
                extent.Add(tr);
                if (draft.Kind == PdfAnnotationKind.Cloud)
                {
                    pad = 20; // the bumps stick out
                    AddInk(annotation, [bl, new UserPoint(tr.X, bl.Y), tr, new UserPoint(bl.X, tr.Y), bl]);
                    appearance = AnnotationAppearance.Cloud(draft.Color, bl, tr, draft.Width);
                }
                else
                {
                    appearance = draft.Kind == PdfAnnotationKind.Rectangle
                        ? AnnotationAppearance.Rectangle(draft.Color, bl, tr, draft.Width)
                        : AnnotationAppearance.Ellipse(draft.Color, bl, tr, draft.Width);
                }

                break;
            }

            case PdfAnnotationKind.Arrow:
            {
                var line = strokes[0];
                var (left, right) = AnnotationAppearance.ArrowHead(line[0], line[^1], draft.Width);
                AddInk(annotation, [line[0], line[^1]]);
                appearance = AnnotationAppearance.Arrow(draft.Color, line[0], line[^1], draft.Width);
                extent.AddRange([line[0], line[^1], left, right]);
                break;
            }

            case PdfAnnotationKind.Polygon:
            {
                var points = strokes[0];
                AddInk(annotation, [.. points, points[0]]);
                appearance = AnnotationAppearance.Polygon(draft.Color, points, draft.Width);
                extent.AddRange(points);
                break;
            }

            default: // pen, marker, line
                foreach (var stroke in strokes)
                {
                    AddInk(annotation, stroke);
                    extent.AddRange(stroke);
                }

                appearance = AnnotationAppearance.Strokes(draft.Color, strokes, draft.Width);
                pad = (draft.Width * 1.5) + 1;
                break;
        }

        SetRect(annotation, extent, pad);
        _ = PdfiumNative.FPDFAnnot_SetBorder(annotation, 0, 0, draft.Width);
        fixed (char* value = appearance)
        {
            _ = PdfiumNative.FPDFAnnot_SetAP(annotation, PdfiumNative.FPDF_ANNOT_APPEARANCEMODE_NORMAL, value);
        }
    }

    private static unsafe void AddInk(nint annotation, IReadOnlyList<UserPoint> stroke)
    {
        var points = stroke.Select(p => new PdfiumNative.FS_POINTF { X = (float)p.X, Y = (float)p.Y }).ToArray();
        fixed (PdfiumNative.FS_POINTF* p = points)
        {
            _ = PdfiumNative.FPDFAnnot_AddInkStroke(annotation, p, (nuint)points.Length);
        }
    }

    private static unsafe void SetRect(nint annotation, List<UserPoint> extent, double pad)
    {
        var rect = new PdfiumNative.FS_RECTF
        {
            Left = (float)(extent.Min(p => p.X) - pad),
            Bottom = (float)(extent.Min(p => p.Y) - pad),
            Right = (float)(extent.Max(p => p.X) + pad),
            Top = (float)(extent.Max(p => p.Y) + pad),
        };
        _ = PdfiumNative.FPDFAnnot_SetRect(annotation, &rect);
    }

    /// <summary>Text boxes, callouts, stamps and measures need fonts, so they're built from real page objects.</summary>
    private void WriteObjects(nint page, nint annotation, PdfAnnotationDraft draft)
    {
        var builder = new AnnotationObjects(_handle, page, annotation, draft.Color);
        NormalizedRect? boxArea = draft.Areas is { Count: > 0 } ? draft.Areas[0] : null;
        var leader = draft.Strokes is { Count: > 0 } ? draft.Strokes[0] : null;
        bool measure = draft.Kind == PdfAnnotationKind.Measure && leader is { Count: >= 2 };
        var from = leader is { Count: >= 2 } ? ToUser(page, leader[0]) : default;
        var to = leader is { Count: >= 2 } ? ToUser(page, leader[^1]) : default;
        var middle = new UserPoint((from.X + to.X) / 2, (from.Y + to.Y) / 2);
        string text = draft.Text ?? string.Empty;

        // The rectangle comes first: PDFium clips what's added to the annotation's current box.
        var extent = new List<UserPoint>();
        if (leader is { Count: >= 2 })
        {
            extent.AddRange([from, to]);
        }

        if (measure)
        {
            var (a, b) = builder.LabelExtent(text, middle, draft.FontSize);
            extent.AddRange([a, b]);
        }
        else if (boxArea is { } box)
        {
            var (bl, tr) = ToUserBox(page, box);
            extent.AddRange([bl, tr]);
        }

        if (extent.Count == 0)
        {
            return;
        }

        SetRect(annotation, extent, draft.Width + 2);

        if (measure)
        {
            builder.Line(from, to, draft.Width);
            builder.Label(text, middle, draft.FontSize);
            return;
        }

        if (boxArea is { } area)
        {
            var (bl, tr) = ToUserBox(page, area);
            if (draft.Kind is PdfAnnotationKind.Stamp or PdfAnnotationKind.Callout)
            {
                builder.Box(bl, tr, draft.Kind == PdfAnnotationKind.Stamp ? Math.Max(draft.Width, 2) : draft.Width);
            }

            if (draft.Kind == PdfAnnotationKind.Stamp)
            {
                builder.CentredText(text, area);
            }
            else
            {
                builder.WrappedText(text, area, draft.FontSize);
            }

            if (draft.Kind == PdfAnnotationKind.Callout && leader is { Count: >= 2 })
            {
                builder.Line(from, to, draft.Width);
            }
        }
    }

    private static PdfAnnotationDraft? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, AnnotationJson.Default.PdfAnnotationDraft);
        }
        catch (JsonException)
        {
            return null; // not ours or damaged: treat it like another app's annotation
        }
    }

    // ================================================================= small helpers

    /// <summary>A point in display fractions to user space (handles /Rotate and crop boxes).</summary>
    internal static unsafe UserPoint ToUser(nint page, PagePoint p)
    {
        double x, y;
        _ = PdfiumNative.FPDF_DeviceToPage(page, 0, 0, DeviceScale, DeviceScale, 0, (int)(p.X * DeviceScale), (int)(p.Y * DeviceScale), &x, &y);
        return new UserPoint(x, y, p.Pressure);
    }

    private static (UserPoint BottomLeft, UserPoint TopRight) ToUserBox(nint page, NormalizedRect r)
    {
        var a = ToUser(page, new PagePoint(r.X, r.Y));
        var b = ToUser(page, new PagePoint(r.Right, r.Bottom));
        return (new UserPoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new UserPoint(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    }

    private static unsafe void SetString(nint annotation, string key, string value)
    {
        byte[] keyBytes = Encoding.ASCII.GetBytes(key + '\0');
        fixed (byte* k = keyBytes)
        fixed (char* v = value)
        {
            _ = PdfiumNative.FPDFAnnot_SetStringValue(annotation, k, v);
        }
    }

    private static unsafe string? GetString(nint annotation, string key)
    {
        byte[] keyBytes = Encoding.ASCII.GetBytes(key + '\0');
        fixed (byte* k = keyBytes)
        {
            uint bytes = PdfiumNative.FPDFAnnot_GetStringValue(annotation, k, null, 0);
            if (bytes <= 2)
            {
                return null;
            }

            var buffer = new char[bytes / 2];
            fixed (char* b = buffer)
            {
                _ = PdfiumNative.FPDFAnnot_GetStringValue(annotation, k, b, bytes);
            }

            return new string(buffer, 0, buffer.Length - 1);
        }
    }
}

[JsonSerializable(typeof(PdfAnnotationDraft))]
internal sealed partial class AnnotationJson : JsonSerializerContext;
