using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Papery.Commands;
using Papery.Services;
using Windows.System;

namespace Papery.Views;

/// <summary>Ctrl+K: type part of an action's name ("rotate", "print") and press Enter.</summary>
internal static class CommandPalette
{
    public static async Task ShowAsync(XamlRoot root, IReadOnlyList<AppCommand> commands)
    {
        var available = commands.Where(c => c.IsAvailable).ToList();
        var box = new TextBox { PlaceholderText = "Type a command, e.g. \"zoom\" or \"print\"", Width = 460 };
        var list = new ListView { MaxHeight = 360, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
        AutomationName(box, "Search commands");
        AutomationName(list, "Commands");

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(list);
        var dialog = new ContentDialog { Content = panel, CloseButtonText = "Close" };
        AppCommand? chosen = null;

        void Refresh()
        {
            string q = box.Text.Trim();
            list.Items.Clear();
            foreach (var command in available.Where(c => q.Length == 0 || c.Matches(q)))
            {
                list.Items.Add(Row(command));
            }

            if (list.Items.Count > 0)
            {
                list.SelectedIndex = 0;
            }
        }

        box.TextChanged += (_, _) => Refresh();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Down && list.SelectedIndex < list.Items.Count - 1)
            {
                list.SelectedIndex++;
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Up && list.SelectedIndex > 0)
            {
                list.SelectedIndex--;
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Enter && list.SelectedItem is FrameworkElement { Tag: AppCommand c })
            {
                chosen = c;
                dialog.Hide();
                e.Handled = true;
            }
        };
        list.ItemClick += (_, e) =>
        {
            chosen = (AppCommand)((FrameworkElement)e.ClickedItem).Tag;
            dialog.Hide();
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        Refresh();
        await Dialogs.ShowAsync(dialog, root);
        chosen?.Execute();
    }

    private static Grid Row(AppCommand command)
    {
        var grid = new Grid { Tag = command, ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = command.Glyph, FontSize = 16, FontFamily = (FontFamily)Application.Current.Resources["PaperyIcons"] });
        var title = new TextBlock { Text = command.Title, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        if (command.Shortcut != null)
        {
            var shortcut = new TextBlock
            {
                Text = command.Shortcut,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"],
            };
            Grid.SetColumn(shortcut, 2);
            grid.Children.Add(shortcut);
        }

        AutomationName(grid, command.Shortcut == null ? command.Title : $"{command.Title}, {command.Shortcut}");
        return grid;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
