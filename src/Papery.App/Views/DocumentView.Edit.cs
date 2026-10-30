using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Edit;
using Papery.Views.Fill;
using Windows.ApplicationModel.DataTransfer;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Papery.Views;

/// <summary>Edit mode: added text, pictures, whiteout and links, plus page numbers, watermarks, Bates numbers and bookmarks.</summary>
public sealed partial class DocumentView
{
    private const int EditModeIndex = 3;
    private const int MaxPictureSide = 2400;
    private const double DefaultTextWidth = 240;

    private static readonly PdfColor[] CoverColours =
    [
        new(255, 255, 255), new(247, 242, 228), new(204, 204, 204), new(96, 96, 96), new(0, 0, 0),
    ];

    private static readonly string UndoFolder = Path.Combine(Path.GetTempPath(), "Papery", "undo");

    private readonly List<string> _undoCopies = [];
    private readonly List<(Canvas Layer, Rectangle Mark)> _linkMarks = [];
    private (bool Set, string? Value) _nextPassword;
    private PdfColor _coverColour = CoverColours[0];
    private PdfAnnotationDraft _textStyle = new(PdfAnnotationKind.TextBox, new PdfColor(33, 33, 33), FontSize: 12);
    private bool _linkToolOn;

    private void BuildEditToolbar()
    {
        var row = EditPieces.ColourRow(CoverColours, _coverColour, colour => Guard.Run(() => ChooseCoverColourAsync(colour), "Choosing a colour"));
        CoverColourHost.Children.Add(row);
        Viewer.AllowDrop = true;
        Viewer.DragOver += OnEditDragOver;
        Viewer.Drop += OnEditDrop;
        Viewer.CurrentPageChanged += (_, _) =>
        {
            if (_modeIndex == EditModeIndex)
            {
                Guard.Run(PreloadAnnotationsAsync, "Reading annotations");
                if (_linkToolOn)
                {
                    Guard.Run(ShowLinkMarksAsync, "Showing links");
                }
            }
        };
    }

    private void EnterEditMode()
    {
        EditToolbar.Visibility = Visibility.Visible;
        ViewToolbar.Visibility = Visibility.Collapsed;
        ComingSoonText.Visibility = Visibility.Collapsed;
        PickEditTool(EditSelect);
        UpdateAnnotateButtons();
        Guard.Run(PreloadAnnotationsAsync, "Reading annotations");
    }

    private void LeaveEditMode()
    {
        EditToolbar.Visibility = Visibility.Collapsed;
        ClearLinkMarks();
        _linkToolOn = false;
        _picker?.Clear();
        UpdateEditSelection();
    }

    /// <summary>Everything in Edit is Pro.</summary>
    private async Task<bool> AllowEditAsync() => XamlRoot != null && await ProDialogs.RequireProAsync(XamlRoot, Tool("edit"));

    /// <summary>One tool is on at a time; finishing with a placing tool returns to Select.</summary>
    private void PickEditTool(ToggleButton on)
    {
        foreach (var toggle in new[] { EditSelect, EditText, EditExisting, EditCover, EditLink })
        {
            toggle.IsChecked = toggle == on;
        }

        ClearLinkMarks();
        _linkToolOn = false;
        _picker?.Clear();
        UpdateEditSelection();
        if (on == EditSelect)
        {
            Viewer.Tool = EnsurePicker();
        }
    }

    private void OnEditSelect(object sender, RoutedEventArgs e) => PickEditTool(EditSelect);

    private void UpdateEditSelection()
    {
        var kind = _picker?.Selected?.Kind;
        EditChangeWords.Visibility = kind is PdfAnnotationKind.TextBox ? Visibility.Visible : Visibility.Collapsed;
        EditTurn.Visibility = kind is PdfAnnotationKind.Signature or PdfAnnotationKind.Image ? Visibility.Visible : Visibility.Collapsed;
        EditDelete.Visibility = kind != null ? Visibility.Visible : Visibility.Collapsed;
        CoverColourHost.Visibility = EditCover.IsChecked == true || kind == PdfAnnotationKind.Whiteout ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================= text

    private void OnEditText(object sender, RoutedEventArgs e) => Guard.Run(ArmTextAsync, "Adding text");

    private async Task ArmTextAsync()
    {
        if (!await AllowEditAsync())
        {
            PickEditTool(EditSelect);
            return;
        }

        PickEditTool(EditText);
        Viewer.Tool = new ClickPlacer((page, start, end) =>
        {
            PickEditTool(EditSelect);
            Guard.Run(() => AddTextAsync(page, start, end), "Adding text");
        });
    }

    private async Task AddTextAsync(int page, PagePoint start, PagePoint end)
    {
        if (_document is not { } document || XamlRoot == null || await TextDialog.ShowAsync(XamlRoot, _textStyle with { Text = null }) is not { } chosen)
        {
            return;
        }

        _textStyle = chosen with { Text = null, Areas = null };
        var size = document.PageSizes[page];
        bool dragged = Math.Abs(end.X - start.X) * size.Width > 12;
        NormalizedRect box;
        if (dragged)
        {
            double height = Math.Max(Math.Abs(end.Y - start.Y), TextDialog.HeightFor(chosen.Text!, chosen.FontSize, Math.Abs(end.X - start.X) * size.Width) / size.Height);
            box = new NormalizedRect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Min(height, 1 - Math.Min(start.Y, end.Y)));
        }
        else
        {
            double w = Math.Min(DefaultTextWidth / size.Width, 1 - start.X);
            double h = TextDialog.HeightFor(chosen.Text!, chosen.FontSize, w * size.Width) / size.Height;
            double top = start.Y - (chosen.FontSize / size.Height);
            box = new NormalizedRect(start.X, Math.Clamp(top, 0, Math.Max(0, 1 - h)), w, Math.Min(h, 1));
        }

        await AddAsync(page, chosen with { Areas = [box] });
    }

    private void OnEditChangeWords(object sender, RoutedEventArgs e) => Guard.Run(ChangeSelectedWordsAsync, "Changing text");

    private async Task ChangeSelectedWordsAsync()
    {
        if (_document is not { } document || XamlRoot == null || _picker?.Selected is not { } selected)
        {
            return;
        }

        if (await document.GetAnnotationDraftAsync(selected.PageIndex, selected.Name) is not { } old)
        {
            Toaster.Show("This one came from another app, so Papery can't change it", "");
            return;
        }

        if (await TextDialog.ShowAsync(XamlRoot, old) is { } changed)
        {
            await ReplaceAsync(selected, old, changed);
        }
    }

    private async Task ReplaceAsync(PdfAnnotationInfo info, PdfAnnotationDraft old, PdfAnnotationDraft changed)
    {
        if (_document is not { } document)
        {
            return;
        }

        _picker?.Clear();
        UpdateEditSelection();
        await document.ChangeAnnotationAsync(info.PageIndex, info.Name, changed);
        await AfterAnnotationChangeAsync(info.PageIndex);
        Remember(
            async () => { await document.ChangeAnnotationAsync(info.PageIndex, info.Name, old); await AfterAnnotationChangeAsync(info.PageIndex); },
            async () => { await document.ChangeAnnotationAsync(info.PageIndex, info.Name, changed); await AfterAnnotationChangeAsync(info.PageIndex); });
    }

    private void OnEditTurn(object sender, RoutedEventArgs e) => Guard.Run(TurnSelectedAsync, "Turning");

    /// <summary>Turns a picture a little at a time. Pictures from an earlier session can only be moved and resized.</summary>
    private async Task TurnSelectedAsync()
    {
        if (_document is not { } document || _picker?.Selected is not { } selected)
        {
            return;
        }

        if (await document.GetAnnotationDraftAsync(selected.PageIndex, selected.Name) is not { Picture: not null } old)
        {
            Toaster.Show("Pictures saved earlier can be moved and resized, but not turned", "");
            return;
        }

        await ReplaceAsync(selected, old, old with { RotationDegrees = (old.RotationDegrees + 15) % 360 });
    }

    private void OnEditDelete(object sender, RoutedEventArgs e) => Guard.Run(async () =>
    {
        await DeleteSelectedAnnotationAsync();
        UpdateEditSelection();
    }, "Delete");

    // ================================================================= pictures

    private void OnEditImage(object sender, RoutedEventArgs e) => Guard.Run(PickPictureAsync, "Adding a picture");

    private async Task PickPictureAsync()
    {
        if (!await AllowEditAsync())
        {
            return;
        }

        if (await ImageFiles.PickAsync(GetWindowHandle?.Invoke() ?? 0) is { } path)
        {
            await ArmPictureAsync(path);
        }
    }

    private async Task ArmPictureAsync(string path)
    {
        if (await ImageFiles.LoadAsync(path, MaxPictureSide) is not { } picture)
        {
            Toaster.Show("Papery couldn't read that picture", "");
            return;
        }

        PickEditTool(EditSelect);
        Toaster.Show("Click where it goes, or drag to set its size", "");
        Viewer.Tool = new ClickPlacer((page, start, end) =>
        {
            Viewer.Tool = EnsurePicker();
            Guard.Run(() => AddAsync(page, PictureDraft(page, picture, start, end)), "Adding a picture");
        });
    }

    private PdfAnnotationDraft PictureDraft(int page, PdfBitmap picture, PagePoint start, PagePoint end)
    {
        var size = _document!.PageSizes[page];
        double aspect = (double)picture.Height / picture.Width;
        double dragged = Math.Abs(end.X - start.X) * size.Width;
        double width = dragged > 12 ? dragged : Math.Min(size.Width * 0.4, picture.Width * 0.75); // a pixel is 3/4 of a point at 96 dpi
        if (width * aspect > size.Height)
        {
            width = size.Height / aspect;
        }

        double w = width / size.Width, h = width * aspect / size.Height;
        double x = dragged > 12 ? Math.Min(start.X, end.X) : start.X - (w / 2);
        double y = dragged > 12 ? Math.Min(start.Y, end.Y) : start.Y - (h / 2);
        var box = new NormalizedRect(Math.Clamp(x, 0, Math.Max(0, 1 - w)), Math.Clamp(y, 0, Math.Max(0, 1 - h)), w, h);
        return new PdfAnnotationDraft(PdfAnnotationKind.Image, new PdfColor(0, 0, 0), [box], Picture: picture);
    }

    private void OnEditDragOver(object sender, DragEventArgs e)
    {
        if (_modeIndex == EditModeIndex && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private async void OnEditDrop(object sender, DragEventArgs e)
    {
        if (_modeIndex != EditModeIndex || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return; // anywhere else, dropped files open as usual
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Select(i => i.Path).FirstOrDefault(ImageFiles.IsImage) is { } path && await AllowEditAsync())
            {
                await ArmPictureAsync(path);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Dropping a picture failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ================================================================= whiteout

    private void OnEditCover(object sender, RoutedEventArgs e) => Guard.Run(ArmCoverAsync, "Whiteout");

    private async Task ArmCoverAsync()
    {
        if (!await AllowEditAsync())
        {
            PickEditTool(EditSelect);
            return;
        }

        PickEditTool(EditCover);
        Toaster.Show("Drag over what to cover. It only hides it: the words underneath stay in the file", "", seconds: 7);
        Viewer.Tool = new ClickPlacer((page, start, end) =>
        {
            var box = new NormalizedRect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
            var size = _document!.PageSizes[page];
            if (box.Width * size.Width >= 3 && box.Height * size.Height >= 3)
            {
                Guard.Run(() => AddAsync(page, new PdfAnnotationDraft(PdfAnnotationKind.Whiteout, _coverColour, [box])), "Whiteout");
            }
        });
    }

    private async Task ChooseCoverColourAsync(PdfColor colour)
    {
        _coverColour = colour;
        if (_picker?.Selected is { Kind: PdfAnnotationKind.Whiteout } selected)
        {
            await RecolourAsync(selected, colour);
        }
    }

    // ================================================================= links

    private void OnEditLink(object sender, RoutedEventArgs e) => Guard.Run(ArmLinkAsync, "Links");

    private async Task ArmLinkAsync()
    {
        if (!await AllowEditAsync())
        {
            PickEditTool(EditSelect);
            return;
        }

        PickEditTool(EditLink);
        _linkToolOn = true;
        await ShowLinkMarksAsync();
        Toaster.Show("Drag a box to make a link, or click one to change it", "");
        Viewer.Tool = new ClickPlacer((page, start, end) => Guard.Run(() => LinkAtAsync(page, start, end), "Links"));
    }

    private async Task ShowLinkMarksAsync()
    {
        ClearLinkMarks();
        if (_document is not { } document)
        {
            return;
        }

        int current = Viewer.CurrentPage;
        for (int page = Math.Max(0, current - 2); page <= Math.Min(document.PageCount - 1, current + 2); page++)
        {
            var links = await document.GetLinksAsync(page);
            if (!_linkToolOn || _document != document || Viewer.ToolLayerFor(page) is not { } layer)
            {
                continue;
            }

            foreach (var link in links.Where(l => !l.IsDetected))
            {
                var mark = new Rectangle
                {
                    Width = link.Area.Width * layer.ActualWidth,
                    Height = link.Area.Height * layer.ActualHeight,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x1E, 0x88, 0xE5)),
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1E, 0x88, 0xE5)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(mark, link.Area.X * layer.ActualWidth);
                Canvas.SetTop(mark, link.Area.Y * layer.ActualHeight);
                layer.Children.Add(mark);
                _linkMarks.Add((layer, mark));
            }
        }
    }

    private void ClearLinkMarks()
    {
        foreach (var (layer, mark) in _linkMarks)
        {
            layer.Children.Remove(mark);
        }

        _linkMarks.Clear();
    }

    private async Task LinkAtAsync(int page, PagePoint start, PagePoint end)
    {
        if (_document is not { } document || XamlRoot == null)
        {
            return;
        }

        var size = document.PageSizes[page];
        bool dragged = Math.Abs(end.X - start.X) * size.Width > 6 && Math.Abs(end.Y - start.Y) * size.Height > 6;
        if (dragged)
        {
            var area = new NormalizedRect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
            if (await LinkDialog.ShowAsync(XamlRoot, null, document.PageCount) is { Remove: false } made)
            {
                await ChangeFileAsync("Link added", "", input => PdfEngine.Shared.EditLinksAsync(input, [], [new NewPdfLink(page, area, made.Uri, made.TargetPage)], FilePath));
                await ShowLinkMarksAsync();
            }

            return;
        }

        var hit = (await document.GetLinksAsync(page)).FirstOrDefault(l =>
            start.X >= l.Area.X && start.X <= l.Area.Right && start.Y >= l.Area.Y && start.Y <= l.Area.Bottom);
        if (hit == null)
        {
            Toaster.Show("Drag a box to make a link", "");
            return;
        }

        if (hit.IsDetected)
        {
            Toaster.Show("This is a web address in the text. Papery makes it clickable, so there's no link to change", "");
            return;
        }

        if (await LinkDialog.ShowAsync(XamlRoot, hit, document.PageCount) is not { } choice)
        {
            return;
        }

        PdfLinkRemoval[] remove = [new(page, hit.Area)];
        await ChangeFileAsync(choice.Remove ? "Link removed" : "Link changed", "", input => PdfEngine.Shared.EditLinksAsync(
            input,
            remove,
            choice.Remove ? [] : [new NewPdfLink(page, hit.Area, choice.Uri, choice.TargetPage)],
            FilePath));
        if (_linkToolOn)
        {
            await ShowLinkMarksAsync();
        }
    }

    // ================================================================= whole-document tools

    private void OnEditStamp(object sender, RoutedEventArgs e)
    {
        var kind = Enum.Parse<StampKind>((string)((FrameworkElement)sender).Tag);
        Guard.Run(() => StampAsync(kind), "Adding to pages");
    }

    private async Task StampAsync(StampKind kind)
    {
        if (_document is not { } document || XamlRoot == null || !await AllowEditAsync())
        {
            return;
        }

        if (await StampDialog.ShowAsync(XamlRoot, GetWindowHandle?.Invoke() ?? 0, kind, document) is { } stamp)
        {
            string done = kind switch
            {
                StampKind.PageNumbers => "Page numbers added",
                StampKind.HeaderFooter => "Added to the pages",
                StampKind.Bates => "Bates numbers added",
                _ => "Watermark added",
            };
            await ChangeFileAsync(done, "", input => PdfEngine.Shared.StampPagesAsync(input, stamp, FilePath));
        }
    }

    private void OnEditBookmarks(object sender, RoutedEventArgs e) => Guard.Run(EditBookmarksAsync, "Bookmarks");

    private async Task EditBookmarksAsync()
    {
        if (_document is not { } document || XamlRoot == null || !await AllowEditAsync())
        {
            return;
        }

        var outline = await document.GetOutlineAsync();
        if (await BookmarksDialog.ShowAsync(XamlRoot, outline, Viewer.CurrentPage, document.PageCount) is { } tree)
        {
            await ChangeFileAsync("Bookmarks saved", "", input => PdfEngine.Shared.SetOutlineAsync(input, tree, FilePath));
        }
    }

    // ================================================================= changes to the whole file

    /// <summary>
    /// Runs a change that rewrites the file. Unsaved work is saved first, and copies from before and after
    /// make it one step of the same undo as everything else. Returns false if it didn't happen.
    /// </summary>
    private async Task<bool> ChangeFileAsync(string done, string glyph, Func<PdfInputFile, Task> change, string? passwordAfter = null, bool changesPassword = false)
    {
        if (!await CheckCanChangeAsync() || !await SaveAnnotationsFirstAsync("This") || !await ConfirmChangingOriginalAsync() || _document is not { } document)
        {
            return false;
        }

        document = _document!;

        string? before = null;
        try
        {
            before = NewUndoCopy();
            File.Copy(FilePath, before, overwrite: true);
            await change(new PdfInputFile(FilePath, document.Password));
            string after = NewUndoCopy();
            File.Copy(FilePath, after, overwrite: true);
            string? passwordBefore = document.Password;
            string? passwordNow = changesPassword ? passwordAfter : passwordBefore;
            string earlier = before;
            _nextPassword = (true, passwordNow);
            await ReloadAsync();
            Remember(
                () => RestoreFileAsync(earlier, passwordBefore),
                () => RestoreFileAsync(after, passwordNow));
            Toaster.Show(done, glyph, "Undo", () => Guard.Run(UndoAnnotationAsync, "Undo"));
            return true;
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            await EditErrors.ShowAsync(ex, XamlRoot);
            return false;
        }
    }

    private async Task RestoreFileAsync(string copy, string? password)
    {
        try
        {
            await PdfEngine.Shared.RestoreCopyAsync(copy, password, FilePath);
        }
        catch (Exception ex) when (ex is PdfEditException or PdfOpenException or IOException or UnauthorizedAccessException)
        {
            await EditErrors.ShowAsync(ex, XamlRoot);
            return;
        }

        _nextPassword = (true, password);
        await ReloadAsync();
    }

    private string NewUndoCopy()
    {
        Directory.CreateDirectory(UndoFolder);
        string path = Path.Combine(UndoFolder, $"{Guid.NewGuid():N}.pdf");
        _undoCopies.Add(path);
        return path;
    }

    /// <summary>These are copies of someone's document, so they go when the tab does.</summary>
    private void DeleteUndoCopies()
    {
        foreach (string path in _undoCopies)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Couldn't delete an undo copy: {ex.GetType().Name}");
            }
        }

        _undoCopies.Clear();
    }
}
