using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Papery.Pdf;
using Papery.Pdf.Editing;

namespace Papery.Views.Organise;

/// <summary>What a page tile can ask the Organise view to do (its hover buttons).</summary>
internal interface IOrganiseActions
{
    void RotatePage(OrganisePage page, int quarterTurns);

    void DeletePage(OrganisePage page);
}

/// <summary>
/// One page on the Organise board. Items are never reused for a different page: each has a
/// stable <see cref="Id"/>, so undo can put exactly the same item back where it was.
/// </summary>
public sealed partial class OrganisePage : ObservableObject
{
    internal OrganisePage(int id, PageRef reference, PdfDocument? document, string? fromFile, PdfPageSize size, IOrganiseActions actions)
    {
        Id = id;
        Reference = reference;
        Document = document;
        FromFile = fromFile;
        Size = size;
        Actions = actions;
        QuarterTurns = reference.QuarterTurns;
    }

    public int Id { get; }

    /// <summary>Which input page this is. <see cref="QuarterTurns"/> holds the live rotation.</summary>
    public PageRef Reference { get; }

    /// <summary>Where to render the thumbnail from; null for a blank page.</summary>
    internal PdfDocument? Document { get; }

    /// <summary>File name when the page was brought in from another PDF (shown as a small chip).</summary>
    public string? FromFile { get; }

    /// <summary>The page's size as it looks in its file (before any turns added here).</summary>
    public PdfPageSize Size { get; }

    internal IOrganiseActions Actions { get; }

    public bool IsBlank => Reference.IsBlank;

    public PageRef Current => Reference with { QuarterTurns = QuarterTurns };

    /// <summary>The size after the turns added here (what the saved page will look like).</summary>
    public PdfPageSize TurnedSize => QuarterTurns % 2 == 0 ? Size : new PdfPageSize(Size.Height, Size.Width);

    [ObservableProperty]
    public partial int QuarterTurns { get; set; }

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    [ObservableProperty]
    public partial int Position { get; set; }

    [ObservableProperty]
    public partial double TileSize { get; set; }

    /// <summary>The paper colour behind the image (follows the reading mode, like the viewer).</summary>
    [ObservableProperty]
    public partial Brush? Paper { get; set; }

    public string Label => (Position + 1).ToString(CultureInfo.CurrentCulture);

    public string AccessibleName
    {
        get
        {
            string name = IsBlank ? $"Page {Position + 1}, blank" : $"Page {Position + 1}";
            if (QuarterTurns != 0)
            {
                name += $", turned {QuarterTurns * 90} degrees";
            }

            return FromFile != null ? $"{name}, from {FromFile}" : name;
        }
    }

    internal CancellationTokenSource? Loading { get; set; }

    /// <summary>Set by the view: renders this page's thumbnail.</summary>
    internal Action<OrganisePage>? ImageNeeded { get; set; }

    /// <summary>Called by a tile when it starts showing this page.</summary>
    internal void EnsureImage()
    {
        if (Image == null && !IsBlank)
        {
            ImageNeeded?.Invoke(this);
        }
    }

    /// <summary>The pixel width the current <see cref="Image"/> was rendered for (0 = none).</summary>
    internal int RenderedWidth { get; set; }

    partial void OnPositionChanged(int value)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(AccessibleName));
    }

    partial void OnQuarterTurnsChanged(int value) => OnPropertyChanged(nameof(AccessibleName));
}
