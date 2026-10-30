using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Papery.Views;

/// <summary>Help tab (F1): the keyboard shortcuts, grouped, plus a way to start the tour.</summary>
public sealed partial class HelpView : UserControl
{
    private static readonly (string Section, (string Keys, string Action)[] Rows)[] Shortcuts =
    [
        ("Files and tabs",
        [
            ("Ctrl + O", "Open a PDF"),
            ("Drag and drop", "Drop PDFs anywhere in the window to open them"),
            ("Ctrl + T", "New tab"),
            ("Ctrl + W", "Close the tab"),
            ("Ctrl + Tab / Ctrl + Shift + Tab", "Next / previous tab"),
            ("Drag a tab out", "Open it in its own window"),
            ("Ctrl + P", "Print"),
            ("Ctrl + D", "Document properties"),
        ]),
        ("Moving around",
        [
            ("Page Up / Page Down", "Scroll a screen at a time"),
            ("Home / End", "First / last page"),
            ("Ctrl + G", "Go to a page number"),
            ("← / →", "Previous / next page in single and two-page views"),
            ("Click a bookmark or thumbnail", "Jump to that page"),
        ]),
        ("Zoom and view",
        [
            ("Ctrl + plus / minus", "Zoom in / out"),
            ("Ctrl + mouse wheel, or pinch", "Zoom smoothly"),
            ("Ctrl + 0", "Fit the whole page"),
            ("Ctrl + 1", "Actual size (100%)"),
            ("Ctrl + 2", "Fit the page width"),
            ("F5", "Present full screen (Esc to leave)"),
        ]),
        ("Text and search",
        [
            ("Drag across text", "Select it"),
            ("Double-click a word", "Select the word"),
            ("Ctrl + A", "Select all text on the page"),
            ("Ctrl + C", "Copy the selected text"),
            ("Ctrl + F", "Search the document"),
            ("F3 / Shift + F3", "Next / previous result"),
        ]),
        ("Organising pages",
        [
            ("Drag a page", "Move it (several selected pages move together)"),
            ("Ctrl + R / Ctrl + L", "Turn right / left"),
            ("Delete", "Remove the selected pages"),
            ("Ctrl + D", "Duplicate the selected pages"),
            ("Ctrl + Z / Ctrl + Y", "Undo / redo"),
            ("Ctrl + mouse wheel", "Bigger or smaller pages"),
            ("Drop PDFs on the pages", "Add their pages right there"),
            ("Ctrl + S", "Save"),
            ("Esc", "Back to reading"),
        ]),
        ("Annotating",
        [
            ("Drag across text", "Highlight, underline or strike it, with that tool chosen"),
            ("Select tool, then Delete", "Remove an annotation"),
            ("Select tool, then drag", "Move an annotation"),
            ("Ctrl + Z / Ctrl + Y", "Undo / redo"),
            ("Ctrl + S", "Save annotations into the file"),
        ]),
        ("Anything else",
        [
            ("Ctrl + K", "Command palette: type what you want to do, e.g. \"dark\" or \"print\""),
            ("F1", "This help page"),
            ("Ctrl + ,", "Settings"),
        ]),
    ];

    public HelpView()
    {
        InitializeComponent();
        foreach (var (section, rows) in Shortcuts)
        {
            Sections.Children.Add(new TextBlock { Text = section, Style = (Style)Application.Current.Resources["SectionHeaderStyle"] });
            var card = new Border { Style = (Style)Application.Current.Resources["SettingsCardStyle"] };
            var grid = new Grid { ColumnSpacing = 24, RowSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var (keys, action) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                int row = grid.RowDefinitions.Count - 1;
                var keyText = new TextBlock { Text = keys, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                var actionText = new TextBlock
                {
                    Text = action,
                    Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
                };
                Grid.SetRow(keyText, row);
                Grid.SetRow(actionText, row);
                Grid.SetColumn(actionText, 1);
                grid.Children.Add(keyText);
                grid.Children.Add(actionText);
            }

            card.Child = grid;
            Sections.Children.Add(card);
        }
    }

    /// <summary>The user asked to see the home screen tour.</summary>
    public event EventHandler? HomeTourRequested;

    private void OnHomeTourClick(object sender, RoutedEventArgs e) => HomeTourRequested?.Invoke(this, EventArgs.Empty);
}
