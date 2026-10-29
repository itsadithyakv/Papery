namespace Papery.Commands;

/// <summary>A tool shown on the home screen.</summary>
/// <param name="Key">Stable id, used in code and settings.</param>
/// <param name="IsPro">Locked until Papery Pro is bought (enforced from stage b).</param>
/// <param name="IsBuilt">False while the tool is still on the build plan; opening it just opens the file.</param>
public sealed record ToolInfo(string Key, string Name, string Description, string Glyph, bool IsPro, bool IsBuilt)
{
    public string AccessibleName => $"{Name}. {Description}";
}

/// <summary>Every tool, in home-screen order. The one place to add or rename a tool.</summary>
internal static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolInfo> All =
    [
        new("organise", "Organise pages", "Reorder, rotate, delete and extract pages", "", IsPro: true, IsBuilt: false),
        new("merge", "Merge", "Combine PDFs and images into one file", "", IsPro: true, IsBuilt: false),
        new("split", "Split", "Break a PDF into smaller files", "", IsPro: true, IsBuilt: false),
        new("compress", "Compress", "Make files smaller for email and uploads", "", IsPro: true, IsBuilt: false),
        new("crop", "Crop & resize", "Trim margins and change the page size", "", IsPro: true, IsBuilt: false),
        new("annotate", "Highlight & comment", "Highlight text and add sticky notes", "", IsPro: false, IsBuilt: false),
        new("draw", "Draw & stamp", "Pen, shapes, text boxes and stamps", "", IsPro: true, IsBuilt: false),
        new("edit", "Edit", "Add text and images, white out, page numbers", "", IsPro: true, IsBuilt: false),
        new("fill", "Fill forms", "Type into form fields and tick boxes", "", IsPro: false, IsBuilt: false),
        new("sign", "Sign", "Draw, type or import your signature", "", IsPro: true, IsBuilt: false),
        new("images-to-pdf", "Images to PDF", "Turn photos and scans into a PDF", "", IsPro: true, IsBuilt: false),
        new("pdf-to-images", "PDF to images", "Save pages as PNG or JPG", "", IsPro: true, IsBuilt: false),
        new("scan", "Scan to PDF", "From a scanner or your camera", "", IsPro: true, IsBuilt: false),
        new("ocr", "Make searchable", "Recognise text in scanned pages", "", IsPro: true, IsBuilt: false),
        new("protect", "Protect", "Add or remove a password", "", IsPro: true, IsBuilt: false),
        new("redact", "Redact", "Permanently remove private information", "", IsPro: true, IsBuilt: false),
        new("batch", "Batch", "Run one action on many files at once", "", IsPro: true, IsBuilt: false),
    ];
}
