using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace Papery.Pdf.Editing;

/// <summary>A new form field, in fractions of the page as displayed.</summary>
public sealed record NewFormField(int PageIndex, PdfFormFieldKind Kind, NormalizedRect Area, string Name, IReadOnlyList<string>? Options = null, bool IsMultiline = false);

/// <summary>
/// Adds form fields to a PDF. PDFium can fill fields but not create them, so this one step writes them
/// with PDFsharp, which is why the file must be saved first. Goes through the checked save.
/// </summary>
internal static class PdfFieldWriter
{
    public static async Task AddAsync(PdfiumThread thread, PdfInputFile input, IReadOnlyList<NewFormField> fields, string outputPath, CancellationToken cancellationToken)
    {
        outputPath = Path.GetFullPath(outputPath);

        // Where each field goes in PDF space, worked out by PDFium so rotation and crop are right.
        var placed = await thread.InvokeAsync(() =>
        {
            using var doc = RawPdf.Load(input.Path, input.Password);
            var result = new List<(NewFormField Field, XRect Rect)>();
            foreach (var group in fields.GroupBy(f => f.PageIndex))
            {
                nint page = Native.PdfiumNative.FPDF_LoadPage(doc.Handle, group.Key);
                if (page == 0)
                {
                    throw new PdfEditException(PdfEditError.BuildFailed, input.Path);
                }

                foreach (var field in group)
                {
                    var a = PdfDocument.ToUser(page, new PagePoint(field.Area.X, field.Area.Y));
                    var b = PdfDocument.ToUser(page, new PagePoint(field.Area.Right, field.Area.Bottom));
                    double left = Math.Min(a.X, b.X), bottom = Math.Min(a.Y, b.Y);
                    result.Add((field, new XRect(left, bottom, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y))));
                }

                Native.PdfiumNative.FPDF_ClosePage(page);
            }

            return result;
        }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

        string temp = SafeFile.CreateTemp(outputPath);
        try
        {
            await Task.Run(() =>
            {
                using var doc = input.Password is null
                    ? PdfReader.Open(input.Path, PdfDocumentOpenMode.Modify)
                    : PdfReader.Open(input.Path, input.Password, PdfDocumentOpenMode.Modify);
                var builder = new AcroFormBuilder(doc) { RectsInPdfSpace = true };
                foreach (var (field, rect) in placed)
                {
                    var page = doc.Pages[field.PageIndex];
                    switch (field.Kind)
                    {
                        case PdfFormFieldKind.CheckBox:
                            builder.AddCheckBox(page, field.Name, rect, isChecked: false);
                            break;
                        case PdfFormFieldKind.ComboBox:
                            var options = field.Options is { Count: > 0 } o ? o.ToArray() : [string.Empty];
                            builder.AddCombo(page, field.Name, rect, options, options[0]);
                            break;
                        case PdfFormFieldKind.Signature:
                            builder.AddSignature(page, field.Name, rect);
                            break;
                        default:
                            builder.AddText(page, field.Name, rect, field.IsMultiline);
                            break;
                    }
                }

                using var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16);
                stream.SetLength(0);
                doc.Save(stream, closeStream: false);
                stream.Flush(flushToDisk: true);
            }, cancellationToken).ConfigureAwait(false);

            int pages = await thread.InvokeAsync(() =>
            {
                using var doc = RawPdf.Load(input.Path, input.Password);
                return doc.PageCount;
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await thread.InvokeAsync(() => PdfComposer.Verify(temp, input.Password, pages, outputPath), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SafeFile.Commit(temp, outputPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or PdfSharp.Pdf.IO.PdfReaderException)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, input.Path, ex);
        }
        finally
        {
            SafeFile.TryDelete(temp);
        }
    }
}
