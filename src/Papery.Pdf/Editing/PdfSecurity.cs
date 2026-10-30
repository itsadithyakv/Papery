using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Papery.Pdf.Native;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

namespace Papery.Pdf.Editing;

/// <summary>A password to add, and what people who open with it may do.</summary>
public sealed record PdfProtection(string Password, bool AllowPrinting = true, bool AllowCopying = true, bool AllowEditing = true, string? OwnerPassword = null);

/// <summary>A PDF's description fields.</summary>
public sealed record PdfDescription(string Title, string Author, string Subject, string Keywords);

/// <summary>What's tucked away in a PDF besides its pages.</summary>
public sealed record PdfHiddenData(bool HasMetadata, int Comments, int Attachments, int HiddenLayers, int Scripts);

/// <summary>Which hidden things to take out.</summary>
public sealed record PdfHiddenDataChoice(bool Metadata = true, bool Comments = true, bool Attachments = true, bool HiddenLayers = true, bool Scripts = true);

/// <summary>Passwords, repairs, description fields and hidden data. All through the checked save.</summary>
internal static class PdfSecurity
{
    private static int PageCount(string path, string? password)
    {
        using var doc = RawPdf.Load(path, password);
        return doc.PageCount;
    }

    private static string NewWork() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Papery", "secure", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>AES-256. Without an owner password a random one is made, so the limits can't be lifted by simply asking.</summary>
    public static async Task ProtectAsync(PdfiumThread thread, PdfInputFile input, PdfProtection protection, string outputPath, CancellationToken cancellationToken)
    {
        string work = NewWork();
        try
        {
            string owner = string.IsNullOrEmpty(protection.OwnerPassword) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) : protection.OwnerPassword;
            var permissions = new JsonObject();
            if (!protection.AllowPrinting)
            {
                permissions["print"] = "none";
            }

            if (!protection.AllowCopying)
            {
                permissions["extract"] = "n";
            }

            if (!protection.AllowEditing)
            {
                permissions["modify"] = "none";
            }

            string final = Path.Combine(work, "final.pdf");
            var job = QpdfTool.Job(input.Path, final, input.Password);
            job["encrypt"] = new JsonObject { ["userPassword"] = protection.Password, ["ownerPassword"] = owner, ["256bit"] = permissions };
            await QpdfTool.RunAsync(job, input.Path, cancellationToken).ConfigureAwait(false);
            int pages = await thread.InvokeAsync(() => PageCount(input.Path, input.Password), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await PdfCompressor.CommitAsync(thread, final, protection.Password, pages, outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    public static async Task RemovePasswordAsync(PdfiumThread thread, PdfInputFile input, string outputPath, CancellationToken cancellationToken)
    {
        string work = NewWork();
        try
        {
            string final = Path.Combine(work, "final.pdf");
            var job = QpdfTool.Job(input.Path, final, input.Password);
            job["decrypt"] = string.Empty;
            await QpdfTool.RunAsync(job, input.Path, cancellationToken).ConfigureAwait(false);
            int pages = await thread.InvokeAsync(() => PageCount(input.Path, input.Password), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await PdfCompressor.CommitAsync(thread, final, null, pages, outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    /// <summary>Tries to rebuild a damaged PDF into a new file. Throws if nothing readable comes out.</summary>
    public static async Task RepairAsync(PdfiumThread thread, string inputPath, string? password, string outputPath, CancellationToken cancellationToken)
    {
        string work = NewWork();
        try
        {
            string final = Path.Combine(work, "final.pdf");
            await QpdfTool.RunAsync(QpdfTool.Job(inputPath, final, password), inputPath, cancellationToken).ConfigureAwait(false);
            int pages = await thread.InvokeAsync(() => PageCount(final, password), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await PdfCompressor.CommitAsync(thread, final, password, pages, outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    public static async Task SetMetadataAsync(PdfiumThread thread, PdfInputFile input, PdfDescription metadata, string outputPath, CancellationToken cancellationToken)
    {
        string work = NewWork();
        try
        {
            string final = Path.Combine(work, "final.pdf");
            await Task.Run(() =>
            {
                using var doc = Open(input);
                doc.Info.Title = metadata.Title;
                doc.Info.Author = metadata.Author;
                doc.Info.Subject = metadata.Subject;
                doc.Info.Keywords = metadata.Keywords;

                // The XML copy of these fields would otherwise still say the old values.
                doc.Internals.Catalog.Elements.Remove("/Metadata");
                Save(doc, final);
            }, cancellationToken).ConfigureAwait(false);
            int pages = await thread.InvokeAsync(() => PageCount(input.Path, input.Password), PdfWorkPriority.Background, cancellationToken).ConfigureAwait(false);
            await PdfCompressor.CommitAsync(thread, final, input.Password, pages, outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    public static Task<PdfHiddenData> InspectAsync(PdfInputFile input, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var doc = Open(input);
        var catalog = doc.Internals.Catalog;
        bool metadata = catalog.Elements.ContainsKey("/Metadata") || new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator" }.Any(k => !string.IsNullOrEmpty(doc.Info.Elements.GetString(k)));
        int comments = 0, attachments = EmbeddedFileCount(catalog), scripts = CatalogScripts(catalog);
        foreach (var page in doc.Pages.Cast<PdfPage>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            scripts += page.Elements.ContainsKey("/AA") ? 1 : 0;
            foreach (var annotation in Annotations(page))
            {
                string subtype = annotation.Elements.GetName("/Subtype");
                if (subtype == "/FileAttachment")
                {
                    attachments++;
                }
                else if (subtype is not ("/Link" or "/Widget" or "/Popup"))
                {
                    comments++;
                }

                scripts += HasScript(annotation) ? 1 : 0;
            }
        }

        return new PdfHiddenData(metadata, comments, attachments, HiddenLayerCount(catalog), scripts);
    }, cancellationToken);

    public static async Task RemoveHiddenDataAsync(PdfiumThread thread, PdfInputFile input, PdfHiddenDataChoice choice, string outputPath, CancellationToken cancellationToken)
    {
        string work = NewWork();
        try
        {
            string source = input.Path;
            bool layers = false;
            int pageCount = 0;
            await Task.Run(() =>
            {
                using var doc = Open(input);
                layers = HiddenLayerCount(doc.Internals.Catalog) > 0;
                pageCount = doc.PageCount;
            }, cancellationToken).ConfigureAwait(false);

            // Turning off a layer only hides its content, and deleting the layer's listing would show it. The only sure
            // way to lose it is to keep what's visible and throw the rest away.
            if (choice.HiddenLayers && layers && input.Password == null)
            {
                source = Path.Combine(work, "flattened.pdf");
                await PdfRedactor.ApplyAsync(thread, input, [], Enumerable.Range(0, pageCount).ToArray(), source, cancellationToken).ConfigureAwait(false);
            }

            string cleaned = Path.Combine(work, "cleaned.pdf");
            await Task.Run(() =>
            {
                using var doc = Open(new PdfInputFile(source, input.Password));
                Clean(doc, choice, layers && input.Password == null);
                Save(doc, cleaned);
            }, cancellationToken).ConfigureAwait(false);

            // Rewriting drops everything just cut loose, so it really leaves the file.
            string final = Path.Combine(work, "final.pdf");
            await QpdfTool.RunAsync(QpdfTool.Tight(QpdfTool.Job(cleaned, final, input.Password)), input.Path, cancellationToken).ConfigureAwait(false);
            await PdfCompressor.CommitAsync(thread, final, input.Password, pageCount, outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PdfCompressor.DeleteFolder(work);
        }
    }

    private static void Clean(PdfSharp.Pdf.PdfDocument doc, PdfHiddenDataChoice choice, bool layersFlattened)
    {
        var catalog = doc.Internals.Catalog;
        if (choice.Metadata)
        {
            catalog.Elements.Remove("/Metadata");
            foreach (string key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator", "/Producer", "/CreationDate", "/ModDate", "/Trapped" })
            {
                doc.Info.Elements.Remove(key);
            }
        }

        if (layersFlattened)
        {
            catalog.Elements.Remove("/OCProperties");
        }

        if (choice.Attachments)
        {
            RemoveName(catalog, "/EmbeddedFiles");
            catalog.Elements.Remove("/AF");
        }

        if (choice.Scripts)
        {
            RemoveName(catalog, "/JavaScript");
            catalog.Elements.Remove("/AA");
            if (catalog.Elements.GetDictionary("/OpenAction") is { } open && open.Elements.GetName("/S") is "/JavaScript" or "/Launch")
            {
                catalog.Elements.Remove("/OpenAction");
            }
        }

        foreach (var page in doc.Pages.Cast<PdfPage>())
        {
            if (choice.Scripts)
            {
                page.Elements.Remove("/AA");
            }

            foreach (var annotation in Annotations(page).ToArray())
            {
                string subtype = annotation.Elements.GetName("/Subtype");
                bool attachment = subtype == "/FileAttachment";
                bool comment = !attachment && subtype is not ("/Link" or "/Widget");
                if ((attachment && choice.Attachments) || (comment && choice.Comments))
                {
                    page.Annotations.Remove(annotation);
                }
                else if (choice.Scripts && HasScript(annotation))
                {
                    annotation.Elements.Remove("/AA");
                    if (annotation.Elements.GetDictionary("/A")?.Elements.GetName("/S") is "/JavaScript" or "/Launch")
                    {
                        annotation.Elements.Remove("/A");
                    }
                }
            }
        }
    }

    private static IEnumerable<PdfAnnotation> Annotations(PdfPage page)
    {
        var annotations = page.Annotations;
        for (int i = 0; i < annotations.Count; i++)
        {
            yield return annotations[i];
        }
    }

    private static bool HasScript(PdfDictionary annotation) =>
        annotation.Elements.ContainsKey("/AA") || annotation.Elements.GetDictionary("/A")?.Elements.GetName("/S") is "/JavaScript" or "/Launch";

    private static int EmbeddedFileCount(PdfDictionary catalog)
    {
        var tree = catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/EmbeddedFiles");
        return tree == null ? 0 : Math.Max(1, (tree.Elements.GetArray("/Names")?.Elements.Count ?? 0) / 2);
    }

    private static int CatalogScripts(PdfDictionary catalog)
    {
        int count = catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/JavaScript") != null ? 1 : 0;
        count += catalog.Elements.ContainsKey("/AA") ? 1 : 0;
        count += catalog.Elements.GetDictionary("/OpenAction")?.Elements.GetName("/S") is "/JavaScript" or "/Launch" ? 1 : 0;
        return count;
    }

    private static int HiddenLayerCount(PdfDictionary catalog) =>
        catalog.Elements.GetDictionary("/OCProperties")?.Elements.GetDictionary("/D")?.Elements.GetArray("/OFF")?.Elements.Count ?? 0;

    private static void RemoveName(PdfDictionary catalog, string key)
    {
        var names = catalog.Elements.GetDictionary("/Names");
        names?.Elements.Remove(key);
        if (names is { Elements.Count: 0 })
        {
            catalog.Elements.Remove("/Names");
        }
    }

    private static PdfSharp.Pdf.PdfDocument Open(PdfInputFile input) => input.Password is null
        ? PdfReader.Open(input.Path, PdfDocumentOpenMode.Modify)
        : PdfReader.Open(input.Path, input.Password, PdfDocumentOpenMode.Modify);

    private static void Save(PdfSharp.Pdf.PdfDocument doc, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        doc.Save(stream, closeStream: false);
        stream.Flush(flushToDisk: true);
    }
}
