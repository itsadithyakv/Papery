using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Papery.Pdf.Editing;

namespace Papery.Views.Edit;

/// <summary>Small controls shared by the Edit dialogs.</summary>
internal static class EditPieces
{
    public static readonly PdfColor[] InkColours =
    [
        new(33, 33, 33), new(110, 110, 110), new(229, 57, 53), new(30, 136, 229), new(46, 125, 50), new(16, 32, 96),
    ];

    public static FontFamily FamilyFor(PdfTextFont font) => new(font switch
    {
        PdfTextFont.Serif => "Times New Roman",
        PdfTextFont.Mono => "Courier New",
        _ => "Arial",
    });

    public static ComboBox FontPicker(PdfTextFont selected)
    {
        var box = new ComboBox { MinWidth = 120 };
        box.Items.Add(new ComboBoxItem { Content = "Sans", FontFamily = FamilyFor(PdfTextFont.Sans) });
        box.Items.Add(new ComboBoxItem { Content = "Serif", FontFamily = FamilyFor(PdfTextFont.Serif) });
        box.Items.Add(new ComboBoxItem { Content = "Mono", FontFamily = FamilyFor(PdfTextFont.Mono) });
        box.SelectedIndex = (int)selected;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Font");
        return box;
    }

    public static NumberBox Number(double value, double min, double max, string name, double width = 96) => new()
    {
        Value = value,
        Minimum = min,
        Maximum = max,
        Width = width,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        Header = name,
    };

    public static ToggleButton StyleToggle(string text, bool on, Windows.UI.Text.FontWeight? weight = null, Windows.UI.Text.FontStyle style = Windows.UI.Text.FontStyle.Normal, string? name = null)
    {
        var toggle = new ToggleButton
        {
            Content = new TextBlock { Text = text, FontWeight = weight ?? Microsoft.UI.Text.FontWeights.Normal, FontStyle = style },
            IsChecked = on,
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name ?? text);
        return toggle;
    }

    /// <summary>A row of colour dots. Reports the chosen one and marks it.</summary>
    public static StackPanel ColourRow(IReadOnlyList<PdfColor> colours, PdfColor selected, Action<PdfColor> chosen)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var dots = new List<(RadioButton Button, PdfColor Colour)>();
        foreach (var colour in colours)
        {
            var dot = new RadioButton
            {
                GroupName = $"colours{row.GetHashCode()}",
                IsChecked = colour with { A = 255 } == selected with { A = 255 },
                MinWidth = 0,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Style = (Style)Application.Current.Resources["PaperyColourDotStyle"],
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, colour.R, colour.G, colour.B)),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, $"Colour {colour.R},{colour.G},{colour.B}");
            dot.Checked += (_, _) => chosen(colour);
            dots.Add((dot, colour));
            row.Children.Add(dot);
        }

        return row;
    }
}
