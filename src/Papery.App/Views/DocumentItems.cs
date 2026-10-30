using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Papery.Pdf;

namespace Papery.Views;

/// <summary>One thumbnail in the Pages sidebar. The image is filled in only when it scrolls into view.</summary>
public sealed partial class ThumbnailItem : ObservableObject
{
    public const double ThumbnailWidth = 120;

    public ThumbnailItem(int pageIndex, PdfPageSize size)
    {
        PageIndex = pageIndex;
        Width = ThumbnailWidth;
        Height = Math.Clamp(ThumbnailWidth * size.Height / size.Width, 40, 240);
    }

    public int PageIndex { get; }

    public double Width { get; }

    public double Height { get; }

    public string Label => (PageIndex + 1).ToString(CultureInfo.CurrentCulture);

    public string AccessibleName => $"Page {PageIndex + 1}";

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    internal CancellationTokenSource? Loading { get; set; }
}

/// <summary>A bookmark in the sidebar tree.</summary>
public sealed class OutlineNode
{
    public OutlineNode(PdfOutlineItem item, int depth)
    {
        Title = item.Title;
        PageIndex = item.PageIndex;
        IsExpanded = depth == 0 && item.Children.Count <= 12;
        Children = item.Children.Select(c => new OutlineNode(c, depth + 1)).ToList();
    }

    public string Title { get; }

    public int? PageIndex { get; }

    public bool IsExpanded { get; }

    public List<OutlineNode> Children { get; }
}

/// <summary>An annotation in the Comments list.</summary>
public sealed class CommentItem(Papery.Pdf.Editing.PdfAnnotationInfo info)
{
    public Papery.Pdf.Editing.PdfAnnotationInfo Info { get; } = info;

    public string Title => string.IsNullOrWhiteSpace(Info.Text) ? Info.Kind.ToString() : Info.Text;

    public string PageLabel => $"Page {Info.PageIndex + 1}";

    public SolidColorBrush Swatch => new(Windows.UI.Color.FromArgb(255, Info.Color.R, Info.Color.G, Info.Color.B));

    public string AccessibleName => $"{Info.Kind} on page {Info.PageIndex + 1}: {Info.Text}";
}

/// <summary>A search match in the sidebar list.</summary>
public sealed class SearchResultItem(PdfSearchHit hit)
{
    public PdfSearchHit Hit { get; } = hit;

    public string PageLabel => $"Page {Hit.PageIndex + 1}";

    public string Context => Hit.Context;

    public string AccessibleName => $"Page {Hit.PageIndex + 1}: {Hit.Context}";
}
