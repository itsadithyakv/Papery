using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Papery.Commands;

/// <summary>A family of tools; each has its own colour so the home screen reads at a glance.</summary>
public enum ToolFamily
{
    Pages,
    Size,
    Markup,
    Edit,
    Sign,
    Convert,
    Protect,
}

/// <summary>A tool shown on the home screen.</summary>
/// <param name="Key">Stable id, used in code and settings.</param>
/// <param name="IsPro">Locked until Papery Pro is bought (enforced from stage b).</param>
/// <param name="IsBuilt">False while the tool is still on the build plan; opening it just opens the file.</param>
public sealed record ToolInfo(string Key, string Name, string Description, string Glyph, ToolFamily Family, bool IsPro, bool IsBuilt)
{
    public string AccessibleName => $"{Name}. {Description}";

    /// <summary>The family colour (icons are graphics, so 3:1 contrast is the target; these all pass on both themes).</summary>
    public SolidColorBrush TintBrush => new(Tint);

    /// <summary>The same colour, faint, for the rounded tile behind the icon.</summary>
    public SolidColorBrush TintSubtleBrush => new(Color.FromArgb(0x26, Tint.R, Tint.G, Tint.B));

    private Color Tint => Family switch
    {
        ToolFamily.Pages => Color.FromArgb(255, 0x3B, 0x82, 0xC4),   // sky
        ToolFamily.Size => Color.FromArgb(255, 0x2A, 0x9D, 0x8F),    // teal
        ToolFamily.Markup => Color.FromArgb(255, 0xE0, 0x8E, 0x1F),  // amber
        ToolFamily.Edit => Color.FromArgb(255, 0x8E, 0x5B, 0xB5),    // plum
        ToolFamily.Sign => Color.FromArgb(255, 0xE5, 0x53, 0x3D),    // coral
        ToolFamily.Convert => Color.FromArgb(255, 0x4E, 0x9A, 0x6B), // sage
        _ => Color.FromArgb(255, 0x6B, 0x7A, 0x8F),                  // slate (protect)
    };
}

/// <summary>Every tool, in home-screen order. The one place to add or rename a tool.</summary>
internal static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolInfo> All =
    [
        new("organise", "Organise pages", "Reorder, rotate, delete", "", ToolFamily.Pages, IsPro: true, IsBuilt: true),
        new("merge", "Merge", "Many files into one", "", ToolFamily.Pages, IsPro: true, IsBuilt: true),
        new("split", "Split", "One file into many", "", ToolFamily.Pages, IsPro: true, IsBuilt: true),
        new("crop", "Crop & resize", "Trim margins, change size", "", ToolFamily.Pages, IsPro: true, IsBuilt: true),
        new("compress", "Compress", "Smaller files to share", "", ToolFamily.Size, IsPro: true, IsBuilt: false),
        new("annotate", "Highlight & comment", "Mark up and add notes", "", ToolFamily.Markup, IsPro: false, IsBuilt: true),
        new("draw", "Draw & stamp", "Pen, shapes and stamps", "", ToolFamily.Markup, IsPro: true, IsBuilt: true),
        new("edit", "Edit", "Text, images, page numbers", "", ToolFamily.Edit, IsPro: true, IsBuilt: true),
        new("fill", "Fill forms", "Type into any form", "", ToolFamily.Sign, IsPro: false, IsBuilt: true),
        new("sign", "Sign", "Add your signature", "", ToolFamily.Sign, IsPro: true, IsBuilt: true),
        new("images-to-pdf", "Images to PDF", "Photos become a PDF", "", ToolFamily.Convert, IsPro: true, IsBuilt: false),
        new("pdf-to-images", "PDF to images", "Pages and pictures out", "", ToolFamily.Convert, IsPro: true, IsBuilt: false),
        new("scan", "Scan to PDF", "Scanner or camera", "", ToolFamily.Convert, IsPro: true, IsBuilt: false),
        new("ocr", "Make searchable", "Read text in scans", "", ToolFamily.Convert, IsPro: true, IsBuilt: false),
        new("protect", "Protect", "Add or remove a password", "", ToolFamily.Protect, IsPro: true, IsBuilt: false),
        new("redact", "Redact", "Remove private details", "", ToolFamily.Protect, IsPro: true, IsBuilt: false),
        new("batch", "Batch", "One action, many files", "", ToolFamily.Size, IsPro: true, IsBuilt: false),
    ];
}
