namespace Papery.Pdf;

/// <summary>Why a PDF could not be opened. The UI turns each reason into a friendly message.</summary>
public enum PdfOpenError
{
    /// <summary>The file is missing, locked, or could not be read.</summary>
    FileUnreadable,

    /// <summary>The file is not a PDF, or is too damaged to repair.</summary>
    Damaged,

    /// <summary>The PDF needs a password, or the password given was wrong.</summary>
    PasswordRequired,

    /// <summary>The PDF uses an encryption scheme PDFium does not support.</summary>
    UnsupportedSecurity,

    /// <summary>The file is larger than 4 GB, the limit of PDFium's file access interface.</summary>
    TooLarge,

    Unknown,
}

public sealed class PdfOpenException : Exception
{
    public PdfOpenException(PdfOpenError error, string path)
        : base($"Could not open '{path}': {error}.")
    {
        Error = error;
        FilePath = path;
    }

    public PdfOpenException(PdfOpenError error, string path, Exception inner)
        : base($"Could not open '{path}': {error}.", inner)
    {
        Error = error;
        FilePath = path;
    }

    public PdfOpenError Error { get; }

    public string FilePath { get; }
}
