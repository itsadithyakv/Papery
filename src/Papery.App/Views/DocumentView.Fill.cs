using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Papery.Controls;
using Papery.Core.Imaging;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Fill;

namespace Papery.Views;

/// <summary>Fill &amp; Sign mode: form fields, ticks and dates on flat PDFs, signatures, initials and flatten.</summary>
public sealed partial class DocumentView
{
    private const int FillModeIndex = 4;

    // How big things land on the page, in points.
    private const double MarkSize = 12;
    private const double SignatureWidth = 150;
    private const double InitialsWidth = 48;

    private static readonly PdfColor FillInk = new(16, 32, 96);

    private readonly SignatureStore _signatures = new();
    private FormOverlay? _formOverlay;

    private void BuildFillToolbar()
    {
        _formOverlay = new FormOverlay(() => _document, OnFormAnswerChanged, field => Guard.Run(() => SignFieldAsync(field), "Signing"));
        Viewer.PageShown += (_, e) =>
        {
            e.Layer.Tag = e.Page;
            Guard.Run(() => _formOverlay.ShowAsync(e.Page, e.Layer, e.Width, e.Height), "Showing form fields");
        };
    }

    private void OnFormAnswerChanged(int page)
    {
        Viewer.RefreshPage(page);
        InvalidateThumbnail(page);
        UpdateAnnotateButtons();
    }

    /// <summary>Drawing tools would fight with the fields' controls, so fields step aside while annotating.</summary>
    private void SetFormFieldsActive(bool active) => Viewer.FormFieldsActive = active;

    private void EnterFillMode()
    {
        FillToolbar.Visibility = Visibility.Visible;
        ViewToolbar.Visibility = Visibility.Collapsed;
        ComingSoonText.Visibility = Visibility.Collapsed;
        Viewer.Tool = null;
        UpdateAnnotateButtons();
    }

    private void LeaveFillMode()
    {
        FillToolbar.Visibility = Visibility.Collapsed;
        foreach (var toggle in FillTools.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>())
        {
            toggle.IsChecked = false;
        }
    }

    /// <summary>Everything except filling existing fields is Pro.</summary>
    private async Task<bool> AllowSignAsync() => XamlRoot != null && await ProDialogs.RequireProAsync(XamlRoot, Tool("sign"));

    // ================================================================= clicking things onto a flat page

    /// <summary>Arms a one-click tool: each click on a page adds what <paramref name="make"/> returns.</summary>
    private async Task ArmPlacerAsync(object sender, Func<int, PagePoint, PagePoint, PdfAnnotationDraft?> make)
    {
        var button = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)sender;
        bool on = button.IsChecked == true;
        foreach (var toggle in FillTools.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Where(t => t != button))
        {
            toggle.IsChecked = false;
        }

        if (!on)
        {
            Viewer.Tool = null;
            return;
        }

        if (!await AllowSignAsync())
        {
            button.IsChecked = false;
            Viewer.Tool = null;
            return;
        }

        Viewer.Tool = new ClickPlacer((page, start, end) =>
        {
            if (make(page, start, end) is { } draft)
            {
                Guard.Run(() => AddAsync(page, draft), "Adding to the page");
            }
        });
    }

    private NormalizedRect Around(int page, PagePoint p, double points)
    {
        var size = _document!.PageSizes[page];
        double w = points / size.Width, h = points / size.Height;
        return new NormalizedRect(p.X - (w / 2), p.Y - (h / 2), w, h);
    }

    private PdfAnnotationDraft Mark(int page, PagePoint p, Func<NormalizedRect, PagePoint[][]> shape)
    {
        var box = Around(page, p, MarkSize);
        return new PdfAnnotationDraft(PdfAnnotationKind.Pen, FillInk, Strokes: shape(box), Width: 1.6f);
    }

    private void OnTick(object sender, RoutedEventArgs e) => Guard.Run(() => ArmPlacerAsync(sender, (page, p, _) => Mark(page, p, b =>
        [[new(b.X, b.Y + (b.Height * 0.55)), new(b.X + (b.Width * 0.38), b.Bottom), new(b.Right, b.Y)]])), "Tick");

    private void OnCross(object sender, RoutedEventArgs e) => Guard.Run(() => ArmPlacerAsync(sender, (page, p, _) => Mark(page, p, b =>
        [[new(b.X, b.Y), new(b.Right, b.Bottom)], [new(b.Right, b.Y), new(b.X, b.Bottom)]])), "Cross");

    private void OnDot(object sender, RoutedEventArgs e) => Guard.Run(() => ArmPlacerAsync(sender, (page, p, _) =>
        new PdfAnnotationDraft(PdfAnnotationKind.Pen, FillInk, Strokes: [[p]], Width: 6)), "Dot");

    private void OnDate(object sender, RoutedEventArgs e) => Guard.Run(() => ArmPlacerAsync(sender, (page, p, _) =>
    {
        var size = _document!.PageSizes[page];
        return new PdfAnnotationDraft(
            PdfAnnotationKind.TextBox,
            FillInk,
            [new NormalizedRect(p.X, p.Y - (10 / size.Height), 110 / size.Width, 22 / size.Height)],
            Text: DateTime.Now.ToShortDateString(),
            FontSize: 11);
    }), "Date");

    private void OnTypeHere(object sender, RoutedEventArgs e) => Guard.Run(() => ArmPlacerAsync(sender, (page, p, end) =>
    {
        var size = _document!.PageSizes[page];
        bool dragged = Math.Abs(end.X - p.X) * size.Width > 10;
        var box = dragged
            ? new NormalizedRect(Math.Min(p.X, end.X), Math.Min(p.Y, end.Y), Math.Abs(end.X - p.X), Math.Max(22 / size.Height, Math.Abs(end.Y - p.Y)))
            : new NormalizedRect(p.X, p.Y - (10 / size.Height), Math.Min(220 / size.Width, 1 - p.X), 22 / size.Height);
        Guard.Run(async () =>
        {
            if (await AskForWordsAsync("Text", string.Empty) is { Length: > 0 } text)
            {
                await AddAsync(page, new PdfAnnotationDraft(PdfAnnotationKind.TextBox, FillInk, [box], Text: text, FontSize: 11));
            }
        }, "Typing on the page");
        return null;
    }), "Type here");

    // ================================================================= signatures

    private void OnSignMenuOpening(object sender, object e)
    {
        SignMenu.Children.Clear();
        foreach (var saved in _signatures.Load())
        {
            var picture = new Image { Source = PageBitmaps.ToWriteableBitmap(saved.Picture), Height = 34, MaxWidth = 170, HorizontalAlignment = HorizontalAlignment.Left };
            var row = new Grid { Width = 230 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var place = new Button { Content = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4), Child = picture }, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(place, saved.IsInitials ? "Place these initials" : "Place this signature");
            place.Click += (_, _) =>
            {
                SignFlyout.Hide();
                Guard.Run(() => ArmSignatureAsync(saved), "Placing a signature");
            };
            row.Children.Add(place);
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"], Margin = new Thickness(4, 0, 0, 0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Delete this one");
            remove.Click += (_, _) =>
            {
                _signatures.Delete(saved.Id);
                OnSignMenuOpening(sender, e);
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            SignMenu.Children.Add(row);
        }

        SignMenu.Children.Add(SignMenuButton("New signature", "", () => NewSignatureAsync(initials: false)));
        SignMenu.Children.Add(SignMenuButton("New initials", "", () => NewSignatureAsync(initials: true)));
        SignMenu.Children.Add(SignMenuButton("Initial every page", "", InitialEveryPageAsync));
    }

    private Button SignMenuButton(string text, string glyph, Func<Task> action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        row.Children.Add(new TextBlock { Text = text });
        var button = new Button { Content = row, Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) =>
        {
            SignFlyout.Hide();
            Guard.Run(action, text);
        };
        return button;
    }

    private async Task NewSignatureAsync(bool initials)
    {
        if (!await AllowSignAsync())
        {
            return;
        }

        if (await SignatureDialog.ShowAsync(XamlRoot, GetWindowHandle?.Invoke() ?? 0, initials) is { } made)
        {
            await ArmSignatureAsync(_signatures.Save(made.Picture, made.IsInitials));
        }
    }

    /// <summary>The next click (or drag, to set its size) on a page places the signature.</summary>
    private async Task ArmSignatureAsync(SavedSignature signature)
    {
        if (!await AllowSignAsync())
        {
            return;
        }

        Toaster.Show("Click where it goes, or drag to set its size", "");
        Viewer.Tool = new ClickPlacer((page, start, end) =>
        {
            Viewer.Tool = null;
            var box = SignatureBox(page, signature, start, end);
            Guard.Run(() => AddAsync(page, new PdfAnnotationDraft(PdfAnnotationKind.Signature, FillInk, [box], Picture: signature.Picture)), "Placing a signature");
        });
    }

    private NormalizedRect SignatureBox(int page, SavedSignature signature, PagePoint start, PagePoint end)
    {
        var size = _document!.PageSizes[page];
        double aspect = (double)signature.Picture.Height / signature.Picture.Width;
        double dragged = Math.Abs(end.X - start.X) * size.Width;
        double width = dragged > 12 ? dragged : signature.IsInitials ? InitialsWidth : SignatureWidth;
        double w = width / size.Width, h = width * aspect / size.Height;
        double x = dragged > 12 ? Math.Min(start.X, end.X) : start.X - (w / 2);
        double y = dragged > 12 ? Math.Min(start.Y, end.Y) : start.Y - (h / 2);
        return new NormalizedRect(Math.Clamp(x, 0, Math.Max(0, 1 - w)), Math.Clamp(y, 0, Math.Max(0, 1 - h)), w, h);
    }

    /// <summary>A signature field's button: pick or make a signature and fit it into the field.</summary>
    private async Task SignFieldAsync(PdfFormField field)
    {
        if (!await AllowSignAsync())
        {
            return;
        }

        var saved = _signatures.Load().FirstOrDefault(s => !s.IsInitials);
        if (saved == null)
        {
            if (await SignatureDialog.ShowAsync(XamlRoot, GetWindowHandle?.Invoke() ?? 0, initials: false) is not { } made)
            {
                return;
            }

            saved = _signatures.Save(made.Picture, false);
        }

        var size = _document!.PageSizes[field.PageIndex];
        double pictureAspect = (double)saved.Picture.Width / saved.Picture.Height;
        double fieldW = field.Bounds.Width * size.Width, fieldH = field.Bounds.Height * size.Height;
        double h = Math.Min(fieldH, fieldW / pictureAspect), w = h * pictureAspect;
        var box = new NormalizedRect(field.Bounds.X + ((fieldW - w) / 2 / size.Width), field.Bounds.Y + ((fieldH - h) / 2 / size.Height), w / size.Width, h / size.Height);
        await AddAsync(field.PageIndex, new PdfAnnotationDraft(PdfAnnotationKind.Signature, FillInk, [box], Picture: saved.Picture));
    }

    /// <summary>Your initials in the bottom-right corner of every page, undone together in one step.</summary>
    private async Task InitialEveryPageAsync()
    {
        if (_document is not { } document || !await AllowSignAsync())
        {
            return;
        }

        var initials = _signatures.Load().FirstOrDefault(s => s.IsInitials);
        if (initials == null)
        {
            if (await SignatureDialog.ShowAsync(XamlRoot, GetWindowHandle?.Invoke() ?? 0, initials: true) is not { } made)
            {
                return;
            }

            initials = _signatures.Save(made.Picture, true);
        }

        const double Margin = 28;
        var added = new List<(int Page, string Name)>();
        double aspect = (double)initials.Picture.Height / initials.Picture.Width;
        for (int page = 0; page < document.PageCount; page++)
        {
            var size = document.PageSizes[page];
            double w = InitialsWidth / size.Width, h = InitialsWidth * aspect / size.Height;
            var box = new NormalizedRect(1 - w - (Margin / size.Width), 1 - h - (Margin / size.Height), w, h);
            var info = await document.AddAnnotationAsync(page, new PdfAnnotationDraft(PdfAnnotationKind.Signature, FillInk, [box], Picture: initials.Picture));
            added.Add((page, info.Name));
            Viewer.RefreshPage(page);
            InvalidateThumbnail(page);
            _annotationCache.Remove(page);
        }

        if (CommentsPanel.Visibility == Visibility.Visible)
        {
            await LoadCommentsAsync();
        }

        async Task SetAll(bool hidden)
        {
            foreach (var (page, name) in added)
            {
                await document.SetAnnotationHiddenAsync(page, name, hidden);
                await AfterAnnotationChangeAsync(page);
            }
        }

        Remember(() => SetAll(true), () => SetAll(false));
        Toaster.Show(added.Count == 1 ? "Initialled the page" : $"Initialled {added.Count:N0} pages", "", "Undo", () => Guard.Run(UndoAnnotationAsync, "Undo"));
    }

    // ================================================================= making fields

    private void OnAddField(object sender, RoutedEventArgs e)
    {
        var kind = Enum.Parse<PdfFormFieldKind>((string)((FrameworkElement)sender).Tag);
        Guard.Run(() => ArmAddFieldAsync(kind), "Adding a field");
    }

    private async Task ArmAddFieldAsync(PdfFormFieldKind kind)
    {
        if (!await AllowSignAsync() || !await CheckCanChangeAsync() || !await SaveAnnotationsFirstAsync("Adding a field"))
        {
            return;
        }

        Toaster.Show(kind == PdfFormFieldKind.CheckBox ? "Click where the box goes" : "Drag a box where the field goes", "\uE710");
        Viewer.Tool = new ClickPlacer((page, start, end) =>
        {
            Viewer.Tool = null;
            Guard.Run(() => AddFieldAtAsync(kind, page, start, end), "Adding a field");
        });
    }

    private async Task AddFieldAtAsync(PdfFormFieldKind kind, int page, PagePoint start, PagePoint end)
    {
        var size = _document!.PageSizes[page];
        var box = new NormalizedRect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        bool tooSmall = box.Width * size.Width < 8 || box.Height * size.Height < 8;
        if (kind == PdfFormFieldKind.CheckBox || tooSmall)
        {
            // A click makes a sensible default size.
            var (w, h) = kind switch
            {
                PdfFormFieldKind.CheckBox => (14.0, 14.0),
                PdfFormFieldKind.Signature => (200.0, 50.0),
                _ => (200.0, 22.0),
            };
            box = new NormalizedRect(start.X, start.Y, w / size.Width, h / size.Height);
        }

        string[]? options = null;
        if (kind == PdfFormFieldKind.ComboBox)
        {
            string? typed = await AskForWordsAsync("Choices, one on each line", string.Empty);
            options = (typed ?? string.Empty).Split('\r', '\n').Select(o => o.Trim()).Where(o => o.Length > 0).ToArray();
            if (options.Length == 0)
            {
                return;
            }
        }

        bool multiline = kind == PdfFormFieldKind.Text && box.Height * size.Height > 36;
        await WriteFieldsAsync([new NewFormField(page, kind, box, $"{kind.ToString().ToLowerInvariant()}_{DateTime.Now:HHmmssfff}", options, multiline)]);
    }

    private void OnFindFields(object sender, RoutedEventArgs e) => Guard.Run(FindFieldsAsync, "Finding fields");

    /// <summary>Looks for writing lines, tick boxes and empty boxes, shows them, and offers to make them fillable.</summary>
    private async Task FindFieldsAsync()
    {
        if (_document is not { } document || !await AllowSignAsync() || !await CheckCanChangeAsync() || !await SaveAnnotationsFirstAsync("Finding fields"))
        {
            return;
        }

        document = _document!;
        var found = new List<NewFormField>();
        int number = 1;
        for (int page = 0; page < document.PageCount; page++)
        {
            var size = document.PageSizes[page];
            int w = 1400, h = Math.Max(16, (int)(1400 * size.Height / size.Width));
            var bitmap = await document.RenderPageAsync(page, w, h, PdfRenderOptions.None, PdfWorkPriority.Background);
            var existing = await document.GetFormFieldsAsync(page);
            var detected = await Task.Run(() => PageImageAnalysis.FindFormFields(bitmap.Pixels, bitmap.Width, bitmap.Height, bitmap.Stride));
            foreach (var field in detected)
            {
                var area = new NormalizedRect(field.Area.X, field.Area.Y, field.Area.Width, field.Area.Height);
                if (existing.Any(f => Overlaps(f.Bounds, area)))
                {
                    continue;
                }

                var kind = field.Kind == DetectedFieldKind.CheckBox ? PdfFormFieldKind.CheckBox : PdfFormFieldKind.Text;
                found.Add(new NewFormField(page, kind, area, $"field_{number++}", null, field.Kind == DetectedFieldKind.TextBox));
            }
        }

        if (found.Count == 0)
        {
            Toaster.Show("No lines or boxes to fill were found", "\uE721");
            return;
        }

        // Show what was found on the pages on screen.
        var marks = new List<(Canvas Layer, Rectangle Mark)>();
        foreach (var field in found)
        {
            if (Viewer.ToolLayerFor(field.PageIndex) is { } layer)
            {
                var mark = new Rectangle
                {
                    Width = field.Area.Width * layer.ActualWidth,
                    Height = field.Area.Height * layer.ActualHeight,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xE5, 0x53, 0x3D)),
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x3D)),
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(mark, field.Area.X * layer.ActualWidth);
                Canvas.SetTop(mark, field.Area.Y * layer.ActualHeight);
                layer.Children.Add(mark);
                marks.Add((layer, mark));
            }
        }

        var dialog = new ContentDialog
        {
            Title = found.Count == 1 ? "Found 1 place to fill in" : $"Found {found.Count:N0} places to fill in",
            Content = Dialogs.Wrap("They're marked on the page. Make them fillable fields? This is saved into the file straight away. It works best on clear printed forms, so check the result."),
            PrimaryButtonText = "Make fillable",
            CloseButtonText = "Not now",
        };
        bool accept = await Dialogs.ShowAsync(dialog, XamlRoot) == ContentDialogResult.Primary;
        foreach (var (layer, mark) in marks)
        {
            layer.Children.Remove(mark);
        }

        if (accept)
        {
            await WriteFieldsAsync(found);
        }
    }

    private static bool Overlaps(NormalizedRect a, NormalizedRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private async Task WriteFieldsAsync(List<NewFormField> fields)
    {
        if (_document is not { } document)
        {
            return;
        }

        try
        {
            await PdfEngine.Shared.AddFormFieldsAsync(new PdfInputFile(FilePath, document.Password), fields, FilePath);
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            await EditErrors.ShowAsync(ex, XamlRoot);
            return;
        }

        await ReloadAsync();
        Toaster.Show(fields.Count == 1 ? "Field added and saved" : $"{fields.Count:N0} fields added and saved", "\uE710");
    }

    // ================================================================= flatten

    private void OnFlatten(object sender, RoutedEventArgs e) => Guard.Run(FlattenAsync, "Flattening");

    private async Task FlattenAsync()
    {
        if (_document is not { } document || !await AllowSignAsync() || !await SaveAnnotationsFirstAsync("Flatten"))
        {
            return;
        }

        document = _document!; // saving reopens the file
        var dialog = new ContentDialog
        {
            Title = "Make answers permanent?",
            Content = Dialogs.Wrap("Form answers and annotations become part of the page in a new copy of this file. They can't be changed in the copy. This file stays as it is."),
            PrimaryButtonText = "Save a copy...",
            CloseButtonText = "Cancel",
        };
        if (await Dialogs.ShowAsync(dialog, XamlRoot) != ContentDialogResult.Primary)
        {
            return;
        }

        string name = Path.GetFileNameWithoutExtension(FilePath) + " (flattened).pdf";
        if (await Pickers.PickSavePdfAsync(GetWindowHandle?.Invoke() ?? 0, name, FilePath) is not { } path)
        {
            return;
        }

        try
        {
            await PdfEngine.Shared.FlattenAsync(new PdfInputFile(FilePath, document.Password), path);
            Toaster.Show($"Saved {Path.GetFileName(path)}", "", "Open", () => OpenFileRequested?.Invoke(this, path));
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            Pickers.RemoveIfEmpty(path);
            await EditErrors.ShowAsync(ex, XamlRoot);
        }
    }
}
