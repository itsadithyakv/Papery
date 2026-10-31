using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;

namespace Papery.Views.Fill;

/// <summary>
/// Puts real Windows controls over a page's form fields, so typing, the clipboard, Tab and Narrator
/// all behave normally. Answers go to the document when a field is left.
/// </summary>
internal sealed class FormOverlay(
    Func<PdfDocument?> document,
    Action<int> pageChanged,
    Action<PdfFormField> signatureFieldClicked)
{
    private static readonly SolidColorBrush FieldBrush = new(Windows.UI.Color.FromArgb(255, 0xE9, 0xF0, 0xFB));
    private static readonly SolidColorBrush FieldBorder = new(Windows.UI.Color.FromArgb(255, 0x8F, 0xA6, 0xC8));

    private readonly List<Task> _writing = [];

    /// <summary>Fills a page's layer with controls. The layer is light-themed, because pages are paper.</summary>
    public async Task ShowAsync(int page, Canvas layer, double width, double height)
    {
        if (document() is not { CanFillForms: true, FormType: not PdfFormType.None } doc)
        {
            return;
        }

        var fields = await doc.GetFormFieldsAsync(page);
        if (document() != doc || layer.Tag is not int current || current != page)
        {
            return; // the page scrolled away, or the file reloaded, while reading
        }

        layer.RequestedTheme = ElementTheme.Light;
        layer.Children.Clear();
        foreach (var field in fields.Where(f => !f.IsReadOnly))
        {
            if (Create(doc, field, height, layer, page) is not { } control)
            {
                continue;
            }

            Canvas.SetLeft(control, field.Bounds.X * width);
            Canvas.SetTop(control, field.Bounds.Y * height);
            control.Width = Math.Max(10, field.Bounds.Width * width);
            control.Height = Math.Max(10, field.Bounds.Height * height);
            AutomationProperties.SetName(control, string.IsNullOrEmpty(field.Name) ? "Form field" : field.Name.Replace('_', ' '));
            layer.Children.Add(control);
        }
    }

    private FrameworkElement? Create(PdfDocument doc, PdfFormField field, double pageHeight, Canvas layer, int page) => field.Kind switch
    {
        PdfFormFieldKind.Text when field.IsPassword => Password(doc, field, pageHeight),
        PdfFormFieldKind.Text => Text(doc, field, pageHeight),
        PdfFormFieldKind.CheckBox or PdfFormFieldKind.RadioButton => Toggle(doc, field, layer, page),
        PdfFormFieldKind.ComboBox or PdfFormFieldKind.ListBox => Choice(doc, field, pageHeight),
        PdfFormFieldKind.Signature => SignHere(field, pageHeight),
        _ => null,
    };

    /// <summary>
    /// The page draws a field's answer too. Controls go see-through on hover and focus by default,
    /// which would show both copies at once, so they stay solid in every state.
    /// </summary>
    private static void Solid(FrameworkElement control, params string[] resourceKeys)
    {
        foreach (string key in resourceKeys)
        {
            control.Resources[key] = FieldBrush;
        }
    }

    private static double FontSizeFor(PdfFormField field, double pageHeight) =>
        field.IsMultiline ? 11 : Math.Clamp(field.Bounds.Height * pageHeight * 0.55, 8, 16);

    private TextBox Text(PdfDocument doc, PdfFormField field, double pageHeight)
    {
        var box = new TextBox
        {
            Text = field.Value,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = FontSizeFor(field, pageHeight),
            Background = FieldBrush,
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            AcceptsReturn = field.IsMultiline,
            TextWrapping = field.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsSpellCheckEnabled = false,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Solid(box, "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused");
        string saved = field.Value;
        void WriteNow()
        {
            if (box.Text != saved)
            {
                saved = box.Text;
                Commit(() => doc.SetFieldTextAsync(field.PageIndex, field.Index, saved), field.PageIndex);
            }
        }

        // Written shortly after typing stops, as well as on leaving, so Save never misses the last words.
        var pause = box.DispatcherQueue.CreateTimer();
        pause.Interval = TimeSpan.FromMilliseconds(500);
        pause.IsRepeating = false;
        pause.Tick += (_, _) => WriteNow();
        box.TextChanged += (_, _) =>
        {
            pause.Stop();
            pause.Start();
        };
        box.LostFocus += (_, _) =>
        {
            pause.Stop();
            WriteNow();
        };
        box.Tag = (Action)WriteNow;
        return box;
    }

    /// <summary>Writes whatever is still being typed, and waits until every answer has reached the document.</summary>
    public async Task FlushAsync(Microsoft.UI.Xaml.XamlRoot root)
    {
        if (FocusManager.GetFocusedElement(root) is FrameworkElement { Tag: Action writeNow })
        {
            writeNow();
        }

        await Task.WhenAll(_writing.ToArray());
    }

    private PasswordBox Password(PdfDocument doc, PdfFormField field, double pageHeight)
    {
        var box = new PasswordBox
        {
            Password = field.Value,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = FontSizeFor(field, pageHeight),
            Background = FieldBrush,
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
        };
        Solid(box, "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused");
        string saved = field.Value;
        box.LostFocus += (_, _) =>
        {
            if (box.Password != saved)
            {
                saved = box.Password;
                Commit(() => doc.SetFieldTextAsync(field.PageIndex, field.Index, saved), field.PageIndex);
            }
        };
        return box;
    }

    private static readonly ControlTemplate ClearToggle = (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        """<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="CheckBox"><Grid Background="Transparent" /></ControlTemplate>""");

    private CheckBox Toggle(PdfDocument doc, PdfFormField field, Canvas layer, int page)
    {
        bool radio = field.Kind == PdfFormFieldKind.RadioButton;
        var button = new CheckBox
        {
            IsChecked = field.IsChecked,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Template = ClearToggle, // the page itself draws the box and its tick; this catches the click and the keyboard, and shows the focus ring
        };
        button.Click += (_, _) =>
        {
            bool isChecked = button.IsChecked == true;
            if (radio && !isChecked)
            {
                button.IsChecked = true; // a picked radio button stays picked until another is chosen
                return;
            }

            Commit(async () =>
            {
                await doc.SetFieldCheckedAsync(field.PageIndex, field.Index, isChecked);
                if (radio)
                {
                    await RefreshAsync(doc, layer, page); // the others in its group just changed
                }
            }, field.PageIndex);
        };
        return button;
    }

    /// <summary>Brings radio buttons in line with what the document now says.</summary>
    private static async Task RefreshAsync(PdfDocument doc, Canvas layer, int page)
    {
        var fields = (await doc.GetFormFieldsAsync(page)).Where(f => !f.IsReadOnly && f.Kind != PdfFormFieldKind.Button).ToList();
        int i = 0;
        foreach (var control in layer.Children.OfType<FrameworkElement>())
        {
            if (control is ToggleButton toggle && i < fields.Count)
            {
                toggle.IsChecked = fields[i].IsChecked;
            }

            i++;
        }
    }

    private Button Choice(PdfDocument doc, PdfFormField field, double pageHeight)
    {
        var label = new TextBlock
        {
            Text = field.SelectedOption >= 0 ? field.Options[field.SelectedOption] : field.Value,
            FontSize = FontSizeFor(field, pageHeight),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(label);
        var chevron = new FontIcon { Glyph = "", FontSize = 9, FontFamily = (FontFamily)Application.Current.Resources["PaperyIcons"] };
        Grid.SetColumn(chevron, 1);
        content.Children.Add(chevron);

        var button = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(4, 0, 4, 0),
            Background = FieldBrush,
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
        };
        Solid(button, "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed");
        var menu = new MenuFlyout();
        for (int i = 0; i < field.Options.Count; i++)
        {
            int option = i;
            var item = new MenuFlyoutItem { Text = field.Options[i] };
            item.Click += (_, _) =>
            {
                label.Text = field.Options[option];
                Commit(() => doc.SetFieldOptionAsync(field.PageIndex, field.Index, option), field.PageIndex);
            };
            menu.Items.Add(item);
        }

        button.Flyout = menu;
        return button;
    }

    private Button SignHere(PdfFormField field, double pageHeight)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = "Sign here", FontSize = Math.Clamp(field.Bounds.Height * pageHeight * 0.3, 9, 14) },
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            Background = FieldBrush,
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
        };
        button.Click += (_, _) => signatureFieldClicked(field);
        return button;
    }

    private void Commit(Func<Task> work, int page)
    {
        var finished = new TaskCompletionSource();
        _writing.Add(finished.Task);
        Guard.Run(async () =>
        {
            try
            {
                await work();
                pageChanged(page);
            }
            finally
            {
                _writing.Remove(finished.Task);
                finished.SetResult();
            }
        }, "Saving a form answer");
    }
}
