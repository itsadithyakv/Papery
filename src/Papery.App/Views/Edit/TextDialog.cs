using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Papery.Controls;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views.Edit;

/// <summary>Words to add to a page, with their font, size, colour and alignment.</summary>
internal static class TextDialog
{
    public static async Task<PdfAnnotationDraft?> ShowAsync(XamlRoot root, PdfAnnotationDraft draft)
    {
        var colour = draft.Color;
        var words = new TextBox
        {
            Text = draft.Text ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            PlaceholderText = "Your words",
            IsSpellCheckEnabled = true,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(words, "Your words");

        var font = EditPieces.FontPicker(draft.Font);
        var size = EditPieces.Number(draft.FontSize, 4, 200, "Size");
        var bold = EditPieces.StyleToggle("B", draft.Bold, Microsoft.UI.Text.FontWeights.Bold, name: "Bold");
        var italic = EditPieces.StyleToggle("I", draft.Italic, style: Windows.UI.Text.FontStyle.Italic, name: "Italic");

        var align = new PillBar();
        align.AddIconItem("", "Left");
        align.AddIconItem("", "Centre");
        align.AddIconItem("", "Right");
        align.SelectedIndex = (int)draft.Align;

        font.Header = "Font";
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        styleRow.Children.Add(font);
        styleRow.Children.Add(size);
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
        marks.Children.Add(bold);
        marks.Children.Add(italic);
        styleRow.Children.Add(marks);

        var lastRow = new Grid();
        lastRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lastRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lastRow.Children.Add(EditPieces.ColourRow(EditPieces.InkColours, colour, c => colour = c));
        Grid.SetColumn(align, 1);
        lastRow.Children.Add(align);

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(words);
        panel.Children.Add(styleRow);
        panel.Children.Add(lastRow);

        var dialog = new ContentDialog
        {
            Title = "Text",
            Content = panel,
            PrimaryButtonText = "Done",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Loaded += (_, _) => words.Focus(FocusState.Programmatic);
        if (await Dialogs.ShowAsync(dialog, root) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(words.Text))
        {
            return null;
        }

        return draft with
        {
            Text = words.Text,
            Color = colour,
            Font = (PdfTextFont)Math.Max(0, font.SelectedIndex),
            FontSize = (float)(double.IsNaN(size.Value) ? 12 : size.Value),
            Bold = bold.IsChecked == true,
            Italic = italic.IsChecked == true,
            Align = (PdfTextAlign)Math.Max(0, align.SelectedIndex),
        };
    }

    /// <summary>A box tall enough for the words at a click, before anyone has dragged one out.</summary>
    public static double HeightFor(string text, float fontSize, double width)
    {
        double perLine = Math.Max(1, (width - 8) / (fontSize * 0.5));
        int lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / perLine)));
        return (lines * fontSize * 1.25) + 10;
    }
}
