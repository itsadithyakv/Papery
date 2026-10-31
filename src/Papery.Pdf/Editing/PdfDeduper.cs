using System.Security.Cryptography;
using System.Text;
using Papery.Pdf.Native;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Papery.Pdf.Editing;

/// <summary>
/// Stores identical pictures and fonts once. A logo stamped on every page, or the same font merged in from
/// ten files, otherwise sits in the file again and again. It is only a size saving, so whenever anything
/// about it looks wrong the original is used instead, and the merged file must draw the same to be kept.
/// </summary>
internal static class PdfDeduper
{
    // Below this it isn't worth rewriting the file at all.
    private const long WorthwhileSaving = 4 * 1024;
    private const int SameDrawingCheckSide = 200;
    private const int SameDrawingTolerance = 2;

    /// <summary>Returns the path to use next: a merged copy in the work folder, or the same path when nothing was merged.</summary>
    public static async Task<string> MergeCopiesAsync(PdfiumThread thread, string path, string? password, string work, CancellationToken cancellationToken)
    {
        if (password != null)
        {
            return path; // rewriting would mean handling the encryption twice, for a little size
        }

        string merged = Path.Combine(work, $"merged-{Guid.NewGuid():N}.pdf");
        try
        {
            bool done = await Task.Run(() => TryMerge(path, merged), cancellationToken).ConfigureAwait(false);
            if (!done || !await thread.InvokeAsync(() => DrawsTheSame(path, merged), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false))
            {
                SafeFile.TryDelete(merged);
                return path;
            }

            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeFile.TryDelete(merged);
            return path;
        }
    }

    /// <summary>Merges into a new file. False when there's nothing worth merging or the file is too odd to rewrite.</summary>
    internal static bool TryMerge(string input, string output)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var all = doc.Internals.GetAllObjects();
        long saved = 0;
        var replaced = new Dictionary<PdfObjectID, PdfReference>();

        // More than once, because merging two masks can make the pictures that use them identical in turn.
        for (int round = 0; round < 3; round++)
        {
            var seen = new Dictionary<string, PdfDictionary>();
            bool any = false;
            foreach (var item in all)
            {
                if (item is not PdfDictionary { Stream: { } stream } dictionary || stream.Value is not { Length: > 0 } data
                    || dictionary.Reference is not { } reference || replaced.ContainsKey(reference.ObjectID))
                {
                    continue;
                }

                if (Describe(dictionary, replaced) is not { } description)
                {
                    continue;
                }

                string key = description + Convert.ToHexString(SHA256.HashData(data));
                if (seen.TryGetValue(key, out var first))
                {
                    replaced[reference.ObjectID] = first.Reference!;
                    saved += data.Length;
                    any = true;
                }
                else
                {
                    seen[key] = dictionary;
                }
            }

            if (!any)
            {
                break;
            }
        }

        if (saved < WorthwhileSaving)
        {
            return false;
        }

        var visited = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        foreach (var item in all)
        {
            Point(item, replaced, visited);
        }

        using var stream2 = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        doc.Save(stream2, closeStream: false);
        stream2.Flush(flushToDisk: true);
        return true;
    }

    /// <summary>A text form of a stream's settings, or null if it holds something that can't be compared safely.</summary>
    private static string? Describe(PdfDictionary dictionary, Dictionary<PdfObjectID, PdfReference> replaced)
    {
        // Every other stream type carries meaning beyond its bytes, or is too small to matter.
        string type = dictionary.Elements.GetName("/Type"), subtype = dictionary.Elements.GetName("/Subtype");
        bool picture = subtype == "/Image";
        bool font = dictionary.Elements.ContainsKey("/Length1") || subtype is "/CIDFontType0C" or "/Type1C" or "/OpenType";
        if (!picture && !font || type is "/XRef" or "/ObjStm" or "/Metadata")
        {
            return null;
        }

        var text = new StringBuilder();
        return Write(dictionary, text, replaced, 0) ? text.ToString() : null;
    }

    private static bool Write(PdfItem? item, StringBuilder text, Dictionary<PdfObjectID, PdfReference> replaced, int depth)
    {
        if (depth > 8)
        {
            return false;
        }

        switch (item)
        {
            case PdfReference reference:
                var target = replaced.GetValueOrDefault(reference.ObjectID, reference);
                text.Append(target.ObjectNumber).Append(' ').Append(target.GenerationNumber).Append(" R ");
                return true;
            case PdfDictionary dictionary:
                text.Append("<<");
                foreach (string key in dictionary.Elements.Keys.Where(k => k != "/Length").Order(StringComparer.Ordinal))
                {
                    text.Append(key).Append(' ');
                    if (!Write(dictionary.Elements[key], text, replaced, depth + 1))
                    {
                        return false;
                    }
                }

                text.Append(">>");
                return true;
            case PdfArray array:
                text.Append('[');
                foreach (var element in array.Elements)
                {
                    if (!Write(element, text, replaced, depth + 1))
                    {
                        return false;
                    }
                }

                text.Append(']');
                return true;
            case PdfName or PdfInteger or PdfLongInteger or PdfReal or PdfBoolean or PdfString or PdfNullObject:
                text.Append(item).Append(' ');
                return true;
            default:
                return false;
        }
    }

    /// <summary>Points everything that used a copy at the one kept. The copies then have no users, so saving leaves them out.</summary>
    private static void Point(PdfItem? item, Dictionary<PdfObjectID, PdfReference> replaced, HashSet<PdfItem> visited)
    {
        if (item == null || !visited.Add(item))
        {
            return;
        }

        if (item is PdfDictionary dictionary)
        {
            foreach (string key in dictionary.Elements.Keys.ToArray())
            {
                var value = dictionary.Elements[key];
                if (value is PdfReference reference)
                {
                    if (replaced.TryGetValue(reference.ObjectID, out var target))
                    {
                        dictionary.Elements[key] = target;
                    }
                }
                else
                {
                    Point(value, replaced, visited);
                }
            }
        }
        else if (item is PdfArray array)
        {
            for (int i = 0; i < array.Elements.Count; i++)
            {
                var value = array.Elements[i];
                if (value is PdfReference reference)
                {
                    if (replaced.TryGetValue(reference.ObjectID, out var target))
                    {
                        array.Elements[i] = target;
                    }
                }
                else
                {
                    Point(value, replaced, visited);
                }
            }
        }
    }

    /// <summary>Compares the first, middle and last pages, drawn small. Enough to catch a rewrite gone wrong without slowing down a big file.</summary>
    private static bool DrawsTheSame(string before, string after)
    {
        using var a = RawPdf.Load(before, null);
        using var b = RawPdf.Load(after, null);
        if (a.PageCount != b.PageCount)
        {
            return false;
        }

        foreach (int index in new[] { 0, a.PageCount / 2, a.PageCount - 1 }.Distinct())
        {
            byte[]? first = Draw(a, index), second = Draw(b, index);
            if (first == null || second == null)
            {
                return false;
            }

            for (int i = 0; i < first.Length; i++)
            {
                if (Math.Abs(first[i] - second[i]) > SameDrawingTolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static unsafe byte[]? Draw(RawPdf doc, int index)
    {
        nint page = PdfiumNative.FPDF_LoadPage(doc.Handle, index);
        if (page == 0)
        {
            return null;
        }

        try
        {
            byte[] pixels = new byte[SameDrawingCheckSide * SameDrawingCheckSide * 4];
            fixed (byte* p = pixels)
            {
                nint bitmap = PdfiumNative.FPDFBitmap_CreateEx(SameDrawingCheckSide, SameDrawingCheckSide, PdfiumNative.FPDFBitmap_BGRA, (nint)p, SameDrawingCheckSide * 4);
                if (bitmap == 0)
                {
                    return null;
                }

                _ = PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, SameDrawingCheckSide, SameDrawingCheckSide, 0xFFFFFFFF);
                PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, SameDrawingCheckSide, SameDrawingCheckSide, 0, PdfiumNative.FPDF_ANNOT);
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }

            return pixels;
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }
}
