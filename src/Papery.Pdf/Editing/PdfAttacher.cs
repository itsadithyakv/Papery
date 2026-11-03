using Papery.Pdf.Native;

namespace Papery.Pdf.Editing;

/// <summary>Adds files to a PDF as attachments, or takes them out again.</summary>
internal static class PdfAttacher
{
    // Attachments travel inside the PDF, so a sensible cap keeps a slip of the file picker from making a 4 GB PDF.
    private const long LargestFile = 500L * 1024 * 1024;

    public static async Task ChangeAsync(PdfiumThread thread, PdfInputFile input, IReadOnlyList<string> add, IReadOnlyList<int> remove, string outputPath, CancellationToken cancellationToken)
    {
        var files = new List<(string Name, byte[] Data)>();
        foreach (string path in add)
        {
            if (new FileInfo(path).Length > LargestFile)
            {
                throw new ArgumentException($"Attachments are limited to {LargestFile / (1024 * 1024)} MB each.", nameof(add));
            }

            files.Add((Path.GetFileName(path), await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
        }

        string work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "attach", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string built = Path.Combine(work, "attached.pdf");
            await File.WriteAllBytesAsync(built, [], cancellationToken).ConfigureAwait(false);
            int pages = await thread.InvokeAsync(() =>
            {
                using var doc = RawPdf.Load(input.Path, input.Password);

                // From the end, so the numbers of those still to go don't shift.
                foreach (int index in remove.Distinct().OrderDescending())
                {
                    if (PdfiumNative.FPDFDoc_DeleteAttachment(doc.Handle, index) == 0)
                    {
                        throw new PdfEditException(PdfEditError.BuildFailed, input.Path);
                    }
                }

                var taken = Names(doc);
                foreach (var (name, data) in files)
                {
                    string unique = Unique(name, taken);
                    taken.Add(unique);
                    Attach(doc, unique, data, input.Path);
                }

                PdfComposer.Save(doc, built, outputPath);
                return doc.PageCount;
            }, PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);

            await PdfCompressor.CommitAsync(thread, built, input.Password, pages, outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    private static unsafe void Attach(RawPdf doc, string name, byte[] data, string path)
    {
        nint attachment = PdfiumNative.FPDFDoc_AddAttachment(doc.Handle, name);
        if (attachment == 0)
        {
            throw new PdfEditException(PdfEditError.BuildFailed, path);
        }

        fixed (byte* d = data)
        {
            if (PdfiumNative.FPDFAttachment_SetFile(attachment, doc.Handle, d, (uint)data.Length) == 0)
            {
                throw new PdfEditException(PdfEditError.BuildFailed, path);
            }
        }
    }

    private static unsafe HashSet<string> Names(RawPdf doc)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = PdfiumNative.FPDFDoc_GetAttachmentCount(doc.Handle);
        for (int i = 0; i < count; i++)
        {
            nint attachment = PdfiumNative.FPDFDoc_GetAttachment(doc.Handle, i);
            if (attachment != 0)
            {
                names.Add(PdfDocument.ReadUtf16(buffer => PdfiumNative.FPDFAttachment_GetName(attachment, buffer.Ptr, buffer.Length)));
            }
        }

        return names;
    }

    /// <summary>PDFium refuses a second attachment with the same name, so a second "notes.txt" becomes "notes (2).txt".</summary>
    internal static string Unique(string name, ISet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
