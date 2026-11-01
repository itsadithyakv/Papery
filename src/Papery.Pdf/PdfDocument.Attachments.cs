using Papery.Pdf.Native;

namespace Papery.Pdf;

/// <summary>A file attached to a PDF.</summary>
public sealed record PdfAttachment(int Index, string Name, long Size);

public sealed partial class PdfDocument
{
    /// <summary>How many files are attached. Doesn't unpack them, so it's safe while a file is opening.</summary>
    public Task<int> CountAttachmentsAsync(CancellationToken cancellationToken = default) =>
        Run(() => PdfiumNative.FPDFDoc_GetAttachmentCount(_handle), cancellationToken, PdfWorkPriority.Nearby);

    /// <summary>The files attached to the document as a whole.</summary>
    public unsafe Task<IReadOnlyList<PdfAttachment>> GetAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        return Run<IReadOnlyList<PdfAttachment>>(() =>
        {
            var found = new List<PdfAttachment>();
            int count = PdfiumNative.FPDFDoc_GetAttachmentCount(_handle);
            for (int i = 0; i < count; i++)
            {
                nint attachment = PdfiumNative.FPDFDoc_GetAttachment(_handle, i);
                if (attachment == 0)
                {
                    continue;
                }

                string name = ReadUtf16(buffer => PdfiumNative.FPDFAttachment_GetName(attachment, buffer.Ptr, buffer.Length));
                uint size = 0;
                if (PdfiumNative.FPDFAttachment_GetFile(attachment, null, 0, &size) != 0)
                {
                    found.Add(new PdfAttachment(i, string.IsNullOrWhiteSpace(name) ? $"Attachment {i + 1}" : name, size));
                }
            }

            return found;
        }, cancellationToken, PdfWorkPriority.Nearby);
    }

    /// <summary>An attached file's bytes.</summary>
    public unsafe Task<byte[]> ReadAttachmentAsync(int index, CancellationToken cancellationToken = default)
    {
        return Run(() =>
        {
            nint attachment = PdfiumNative.FPDFDoc_GetAttachment(_handle, index);
            uint size = 0;
            if (attachment == 0 || PdfiumNative.FPDFAttachment_GetFile(attachment, null, 0, &size) == 0)
            {
                throw new InvalidDataException("That attachment can't be read.");
            }

            byte[] data = new byte[size];
            fixed (byte* d = data)
            {
                uint written = 0;
                if (size > 0 && PdfiumNative.FPDFAttachment_GetFile(attachment, d, size, &written) == 0)
                {
                    throw new InvalidDataException("That attachment can't be read.");
                }
            }

            return data;
        }, cancellationToken, PdfWorkPriority.Nearby);
    }
}
