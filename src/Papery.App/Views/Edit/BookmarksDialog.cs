using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views.Edit;

/// <summary>Add, rename, move, nest and delete bookmarks. Returns the new tree, or null if cancelled.</summary>
internal static class BookmarksDialog
{
    public static async Task<IReadOnlyList<PdfOutlineItem>?> ShowAsync(XamlRoot root, IReadOnlyList<PdfOutlineItem> outline, int currentPage, int pageCount)
    {
        var editor = new OutlineEditor(outline);
        var list = new ListView { Height = 280, SelectionMode = ListViewSelectionMode.Single };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Bookmarks");
        var title = new TextBox { Header = "Name" };
        var page = EditPieces.Number(1, 1, pageCount, "Page", 110);
        bool filling = false;

        void Show(int select)
        {
            filling = true;
            list.Items.Clear();
            foreach (var row in editor.Rows)
            {
                var line = new Grid { Margin = new Thickness(row.Depth * 18, 0, 0, 0) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = row.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var number = new TextBlock
                {
                    Text = row.PageIndex is int p ? (p + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                    Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
                };
                Grid.SetColumn(number, 1);
                line.Children.Add(number);
                list.Items.Add(new ListViewItem { Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }

            list.SelectedIndex = editor.Rows.Count == 0 ? -1 : Math.Clamp(select, 0, editor.Rows.Count - 1);
            filling = false;
            LoadRow();
        }

        void LoadRow()
        {
            bool has = list.SelectedIndex >= 0;
            title.IsEnabled = has;
            page.IsEnabled = has;
            filling = true;
            title.Text = has ? editor.Rows[list.SelectedIndex].Title : string.Empty;
            page.Value = has ? (editor.Rows[list.SelectedIndex].PageIndex ?? 0) + 1 : 1;
            filling = false;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (!filling)
            {
                LoadRow();
            }
        };

        title.TextChanged += (_, _) =>
        {
            if (!filling && list.SelectedIndex is >= 0 and var i && title.Text.Length > 0)
            {
                editor.Rename(i, title.Text);
                if (list.Items[i] is ListViewItem { Content: Grid line } && line.Children[0] is TextBlock text)
                {
                    text.Text = title.Text;
                }
            }
        };

        page.ValueChanged += (_, _) =>
        {
            if (!filling && list.SelectedIndex is >= 0 and var i && !double.IsNaN(page.Value))
            {
                editor.SetPage(i, (int)page.Value - 1);
                if (list.Items[i] is ListViewItem { Content: Grid line } && line.Children[1] is TextBlock number)
                {
                    number.Text = ((int)page.Value).ToString(System.Globalization.CultureInfo.CurrentCulture);
                }
            }
        };

        Button Tool(string glyph, string name, Action act)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = glyph, Style = (Style)Application.Current.Resources["ToolIconStyle"] },
                Style = (Style)Application.Current.Resources["ToolButtonStyle"],
            };
            ToolTipService.SetToolTip(button, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => act();
            return button;
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        tools.Children.Add(Tool("", "Add a bookmark to this page", () =>
        {
            int at = editor.Add(list.SelectedIndex, "New bookmark", currentPage);
            Show(at);
            title.Focus(FocusState.Programmatic);
            title.SelectAll();
        }));
        tools.Children.Add(Tool("", "Move up", () => { if (list.SelectedIndex >= 0) { Show(editor.MoveUp(list.SelectedIndex)); } }));
        tools.Children.Add(Tool("", "Move down", () => { if (list.SelectedIndex >= 0) { Show(editor.MoveDown(list.SelectedIndex)); } }));
        tools.Children.Add(Tool("", "Nest under the one above", () =>
        {
            if (list.SelectedIndex is >= 0 and var i)
            {
                editor.Indent(i);
                Show(i);
            }
        }));
        tools.Children.Add(Tool("", "Lift out a level", () =>
        {
            if (list.SelectedIndex is >= 0 and var i)
            {
                editor.Outdent(i);
                Show(i);
            }
        }));
        tools.Children.Add(Tool("", "Delete it and anything nested under it", () =>
        {
            if (list.SelectedIndex is >= 0 and var i)
            {
                editor.Remove(i);
                Show(i);
            }
        }));

        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Width = 300;
        details.Children.Add(title);
        details.Children.Add(page);

        var panel = new StackPanel { Spacing = 10, Width = 430 };
        panel.Children.Add(tools);
        panel.Children.Add(list);
        panel.Children.Add(details);
        Show(0);

        var dialog = new ContentDialog
        {
            Title = "Bookmarks",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await Dialogs.ShowAsync(dialog, root) == ContentDialogResult.Primary ? editor.ToTree() : null;
    }
}
