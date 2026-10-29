namespace Papery.Pdf.Editing;

/// <summary>Why a change could not be saved. The UI turns each reason into a friendly message.</summary>
public enum PdfEditError
{
    /// <summary>The PDF's author turned off changes to its pages (owner permissions).</summary>
    NotAllowed,

    /// <summary>The output could not be written: no permission, disk full, or the folder is gone.</summary>
    CannotWrite,

    /// <summary>Another app has the file open without allowing it to be replaced.</summary>
    FileInUse,

    /// <summary>PDFium could not build the result (an input page is too damaged to copy).</summary>
    BuildFailed,

    /// <summary>The written file did not read back correctly, so it was thrown away.</summary>
    VerifyFailed,
}

public sealed class PdfEditException : Exception
{
    public PdfEditException(PdfEditError error, string path, Exception? inner = null)
        : base($"Could not save '{path}': {error}.", inner)
    {
        Error = error;
        FilePath = path;
    }

    public PdfEditError Error { get; }

    /// <summary>The file the problem is about (an input for <see cref="PdfEditError.NotAllowed"/>, otherwise the output).</summary>
    public string FilePath { get; }
}
