using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Papery.Controls;
using Papery.Pdf;
using Papery.Pdf.Editing;
using Papery.Services;
using Papery.Views.Annotate;
using Windows.System;

namespace Papery.Views;

/// <summary>Annotate mode: the tools, colours, undo and redo, saving, and the comments list.</summary>
public sealed partial class DocumentView
{
    private const int AnnotateModeIndex = 2;

    private static readonly PdfColor[] Swatches =
    [
        new(255, 214, 0), new(255, 122, 99), new(229, 57, 53), new(76, 175, 80), new(30, 136, 229), new(33, 33, 33),
    ];

    // Name, what to show, Free (the rest are Pro), default width. Icons drawn as paths are the shapes themselves.
    private static readonly (PdfAnnotationKind? Kind, string Name, string? Glyph, string? Path, bool Free, float Width)[] AnnotationTools =
    [
        (null, "Select, move or delete", "", null, true, 0),
        (PdfAnnotationKind.Highlight, "Highlight text", "", null, true, 0),
        (PdfAnnotationKind.Note, "Sticky note", "", null, true, 0),
        (PdfAnnotationKind.Underline, "Underline text", "", null, false, 0),
        (PdfAnnotationKind.StrikeOut, "Strike through text", "", null, false, 0),
        (PdfAnnotationKind.Squiggly, "Squiggly underline", null, "M1,8 L4,4 L7,8 L10,4 L13,8 L16,4 L16,7 L13,11 L10,7 L7,11 L4,7 L1,11 Z", false, 0),
        (PdfAnnotationKind.Pen, "Pen", "", null, false, 2),
        (PdfAnnotationKind.Marker, "Highlighter pen", null, "M2,10 Q8,3 14,7 L14,11 Q8,7 2,14 Z", false, 12),
        (PdfAnnotationKind.Rectangle, "Rectangle", null, "M1,3 H15 V13 H1 Z M3,5 V11 H13 V5 Z", false, 2),
        (PdfAnnotationKind.Ellipse, "Ellipse", null, "M8,2 A7,6 0 1 1 8,14 A7,6 0 1 1 8,2 Z M8,4 A5,4 0 1 0 8,12 A5,4 0 1 0 8,4 Z", false, 2),
        (PdfAnnotationKind.Line, "Line", null, "M2,13 L13,2 L14,3 L3,14 Z", false, 2),
        (PdfAnnotationKind.Arrow, "Arrow", null, "M2,13 L10,5 L7,4 L14,2 L12,9 L11,6 L3,14 Z", false, 2),
        (PdfAnnotationKind.Polygon, "Polygon (click each corner, then the first again)", null, "M8,1 L15,6 L12,14 L4,14 L1,6 Z M8,3.5 L3.2,6.8 L5.4,12.5 L10.6,12.5 L12.8,6.8 Z", false, 2),
        (PdfAnnotationKind.Cloud, "Cloud", null, "M4,13 A3,3 0 0 1 3,7 A4,4 0 0 1 10,4 A3,3 0 0 1 13,8 A3,3 0 0 1 12,13 Z", false, 1.5f),
        (PdfAnnotationKind.TextBox, "Text box", "", null, false, 1),
        (PdfAnnotationKind.Callout, "Callout (drag from the thing to where the words go)", "", null, false, 1.5f),
        (PdfAnnotationKind.Stamp, "Stamp", null, "M6,2 H10 L9,8 H13 V11 H3 V8 H7 Z M2,12.5 H14 V14 H2 Z", false, 2),
        (PdfAnnotationKind.Measure, "Measure a distance", "", null, false, 1.5f),
    ];

    private static readonly string[] StampWords = ["APPROVED", "DRAFT", "CONFIDENTIAL", "RECEIVED", "PAID"];

    private readonly Dictionary<ToggleButton, int> _toolButtons = [];
    private readonly Dictionary<int, IReadOnlyList<PdfAnnotationInfo>> _annotationCache = [];
    private readonly Stack<(Func<Task> Undo, Func<Task> Redo)> _annotationUndo = new();
    private readonly Stack<(Func<Task> Undo, Func<Task> Redo)> _annotationRedo = new();
    private readonly ObservableCollection<CommentItem> _comments = [];
    private int _toolIndex = 1;
    private PdfColor _toolColor = Swatches[0];
    private AnnotationPen? _pen;
    private AnnotationPicker? _picker;
    private string _stampWord = StampWords[0];

    // ================================================================= building the toolbar

    private void BuildAnnotateToolbar()
    {
        CommentsList.ItemsSource = _comments;
        for (int i = 0; i < AnnotationTools.Length; i++)
        {
            var def = AnnotationTools[i];
            var button = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["ToolToggleButtonStyle"],
                Content = ToolContent(def.Glyph, def.Path),
            };
            ToolTipService.SetToolTip(button, def.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, def.Name);
            int index = i;
            button.Click += (_, _) => Guard.Run(() => PickToolAsync(index), "Choosing a tool");
            if (def.Kind == PdfAnnotationKind.Stamp)
            {
                var menu = new MenuFlyout();
                foreach (string word in StampWords)
                {
                    var item = new MenuFlyoutItem { Text = word };
                    item.Click += (_, _) => _stampWord = word;
                    menu.Items.Add(item);
                }

                FlyoutBase.SetAttachedFlyout(button, menu);
            }

            _toolButtons[button] = i;
            AnnotateTools.Children.Add(button);
        }

        foreach (var swatch in Swatches)
        {
            var dot = new Button
            {
                Width = 22,
                Height = 22,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(0),
                Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"],
                VerticalAlignment = VerticalAlignment.Center,

                // The colour is the content, so hovering (which repaints a button's background) can't hide it.
                Content = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 18,
                    Height = 18,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, swatch.R, swatch.G, swatch.B)),
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, $"Colour {swatch.R},{swatch.G},{swatch.B}");
            dot.Click += (_, _) => Guard.Run(() => ChooseColourAsync(swatch), "Choosing a colour");
            AnnotateColours.Children.Add(dot);
        }

        Viewer.TextMarked += (_, selection) => Guard.Run(() => MarkTextAsync(selection), "Marking text");
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, (_, a) => { if (_modeIndex == AnnotateModeIndex) { Guard.Run(UndoAnnotationAsync, "Undo"); a.Handled = true; } });
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, (_, a) => { if (_modeIndex == AnnotateModeIndex) { Guard.Run(RedoAnnotationAsync, "Redo"); a.Handled = true; } });
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.None, (_, a) => { if (_modeIndex == AnnotateModeIndex && _picker?.Selected != null) { Guard.Run(DeleteSelectedAnnotationAsync, "Delete"); a.Handled = true; } });
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, (_, a) => { if (_document is { HasAnnotationChanges: true }) { Guard.Run(() => SaveAnnotationsAsync(), "Saving"); a.Handled = true; } });
        Viewer.CurrentPageChanged += (_, _) =>
        {
            if (_modeIndex == AnnotateModeIndex)
            {
                Guard.Run(PreloadAnnotationsAsync, "Reading annotations");
            }
        };
        AppServices.Pro.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateToolLocks);
        UpdateToolLocks();
    }

    private static UIElement ToolContent(string? glyph, string? path)
    {
        if (glyph != null)
        {
            return new FontIcon { Glyph = glyph, Style = (Style)Application.Current.Resources["ToolIconStyle"] };
        }

        return new PathIcon
        {
            Width = 16,
            Height = 16,
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path!),
        };
    }

    /// <summary>Pro tools look a little quieter until unlocked; picking one shows the unlock screen.</summary>
    private void UpdateToolLocks()
    {
        bool pro = AppServices.Pro.IsPro;
        foreach (var (button, index) in _toolButtons)
        {
            bool locked = !pro && !AnnotationTools[index].Free;
            button.Opacity = locked ? 0.55 : 1;
            string name = AnnotationTools[index].Name;
            ToolTipService.SetToolTip(button, locked ? $"{name} (Papery Pro)" : name);
        }
    }

    // ================================================================= entering and leaving

    private void EnterAnnotateMode()
    {
        AnnotateToolbar.Visibility = Visibility.Visible;
        ViewToolbar.Visibility = Visibility.Collapsed;
        ComingSoonText.Visibility = Visibility.Collapsed;
        ApplyTool();
        UpdateAnnotateButtons();
        Guard.Run(PreloadAnnotationsAsync, "Reading annotations");
    }

    private void LeaveAnnotateMode()
    {
        AnnotateToolbar.Visibility = Visibility.Collapsed;
        _pen?.Cancel();
        _picker?.Clear();
        Viewer.Tool = null;
        Viewer.MarkTextOnSelect = false;
    }

    private async Task PickToolAsync(int index)
    {
        var def = AnnotationTools[index];
        if (!def.Free && XamlRoot != null && !await ProDialogs.RequireProAsync(XamlRoot, Tool(def.Kind is PdfAnnotationKind.Pen or PdfAnnotationKind.Marker ? "draw" : "annotate")))
        {
            ApplyTool(); // stay on the tool they had
            return;
        }

        _toolIndex = index;
        ApplyTool();
        if (def.Kind == PdfAnnotationKind.Stamp)
        {
            FlyoutBase.ShowAttachedFlyout(_toolButtons.First(b => b.Value == index).Key);
        }
    }

    private void ApplyTool()
    {
        foreach (var (button, index) in _toolButtons)
        {
            button.IsChecked = index == _toolIndex;
        }

        var def = AnnotationTools[_toolIndex];
        _pen?.Cancel();
        _picker?.Clear();
        _pen = null;
        Viewer.MarkTextOnSelect = false;
        Viewer.Tool = null;
        switch (def.Kind)
        {
            case null:
                _picker ??= new AnnotationPicker(page => _annotationCache.GetValueOrDefault(page) ?? [], (info, bounds) => Guard.Run(() => MoveAnnotationAsync(info, bounds), "Moving"));
                Viewer.Tool = _picker;
                break;
            case PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.StrikeOut or PdfAnnotationKind.Squiggly:
                Viewer.MarkTextOnSelect = true; // drag across text as usual
                break;
            default:
                _pen = new AnnotationPen(def.Kind.Value, ColourFor(def.Kind.Value), def.Width, page => Viewer.Document!.PageSizes[page]);
                _pen.StampText = _stampWord;
                _pen.Drawn += (page, draft) => Guard.Run(() => FinishDrawingAsync(page, draft), "Adding");
                Viewer.Tool = _pen;
                break;
        }
    }

    /// <summary>Highlighters are see-through so the words stay readable.</summary>
    private PdfColor ColourFor(PdfAnnotationKind kind) =>
        kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Marker ? _toolColor with { A = 110 } : _toolColor;

    private async Task ChooseColourAsync(PdfColor colour)
    {
        _toolColor = colour;
        if (_picker?.Selected is { } selected)
        {
            await RecolourAsync(selected, selected.Kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Marker ? colour with { A = 110 } : colour);
        }
        else if (_pen != null)
        {
            _pen.Color = ColourFor(_pen.Kind);
        }
    }

    // ================================================================= adding

    private Task MarkTextAsync(PdfTextSelection selection)
    {
        var kind = AnnotationTools[_toolIndex].Kind;
        return kind == null || _document == null
            ? Task.CompletedTask
            : AddAsync(selection.PageIndex, new PdfAnnotationDraft(kind.Value, ColourFor(kind.Value), selection.Areas));
    }

    private async Task FinishDrawingAsync(int page, PdfAnnotationDraft draft)
    {
        if (draft.Kind is PdfAnnotationKind.Note or PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout)
        {
            string? text = await AskForWordsAsync(draft.Kind == PdfAnnotationKind.Note ? "Note" : "Text", string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            draft = draft with { Text = text };
        }
        else if (draft.Kind == PdfAnnotationKind.Stamp)
        {
            draft = draft with { Text = _stampWord };
        }

        await AddAsync(page, draft);
    }

    private async Task AddAsync(int page, PdfAnnotationDraft draft)
    {
        if (_document is not { } document)
        {
            return;
        }

        var added = await document.AddAnnotationAsync(page, draft);
        await AfterAnnotationChangeAsync(page);
        Remember(
            async () => { await document.SetAnnotationHiddenAsync(page, added.Name, true); await AfterAnnotationChangeAsync(page); },
            async () => { await document.SetAnnotationHiddenAsync(page, added.Name, false); await AfterAnnotationChangeAsync(page); });
    }

    // ================================================================= changing

    private async Task DeleteSelectedAnnotationAsync()
    {
        if (_picker?.Selected is { } selected)
        {
            _picker.Clear();
            await DeleteAsync(selected);
        }
    }

    private async Task DeleteAsync(PdfAnnotationInfo info)
    {
        if (_document is not { } document)
        {
            return;
        }

        await document.SetAnnotationHiddenAsync(info.PageIndex, info.Name, true);
        await AfterAnnotationChangeAsync(info.PageIndex);
        Remember(
            async () => { await document.SetAnnotationHiddenAsync(info.PageIndex, info.Name, false); await AfterAnnotationChangeAsync(info.PageIndex); },
            async () => { await document.SetAnnotationHiddenAsync(info.PageIndex, info.Name, true); await AfterAnnotationChangeAsync(info.PageIndex); });
    }

    private async Task RecolourAsync(PdfAnnotationInfo info, PdfColor colour)
    {
        if (_document is not { } document)
        {
            return;
        }

        var old = info.Color;
        if (!await document.RecolorAnnotationAsync(info.PageIndex, info.Name, colour))
        {
            Toaster.Show("This one came from another app, so Papery can't recolour it", "\uE946");
            return;
        }

        await AfterAnnotationChangeAsync(info.PageIndex);
        Remember(
            async () => { await document.RecolorAnnotationAsync(info.PageIndex, info.Name, old); await AfterAnnotationChangeAsync(info.PageIndex); },
            async () => { await document.RecolorAnnotationAsync(info.PageIndex, info.Name, colour); await AfterAnnotationChangeAsync(info.PageIndex); });
    }

    private async Task MoveAnnotationAsync(PdfAnnotationInfo info, NormalizedRect bounds)
    {
        if (_document is not { } document)
        {
            return;
        }

        var old = info.Bounds;
        await document.MoveAnnotationAsync(info.PageIndex, info.Name, bounds);
        await AfterAnnotationChangeAsync(info.PageIndex);
        Remember(
            async () => { await document.MoveAnnotationAsync(info.PageIndex, info.Name, old); await AfterAnnotationChangeAsync(info.PageIndex); },
            async () => { await document.MoveAnnotationAsync(info.PageIndex, info.Name, bounds); await AfterAnnotationChangeAsync(info.PageIndex); });
    }

    private void Remember(Func<Task> undo, Func<Task> redo)
    {
        _annotationUndo.Push((undo, redo));
        _annotationRedo.Clear();
        UpdateAnnotateButtons();
    }

    private async Task UndoAnnotationAsync()
    {
        if (_annotationUndo.TryPop(out var step))
        {
            _picker?.Clear();
            await step.Undo();
            _annotationRedo.Push(step);
            UpdateAnnotateButtons();
        }
    }

    private async Task RedoAnnotationAsync()
    {
        if (_annotationRedo.TryPop(out var step))
        {
            _picker?.Clear();
            await step.Redo();
            _annotationUndo.Push(step);
            UpdateAnnotateButtons();
        }
    }

    private async Task AfterAnnotationChangeAsync(int page)
    {
        Viewer.RefreshPage(page);
        InvalidateThumbnail(page);
        if (_document is { } document)
        {
            _annotationCache[page] = await document.GetAnnotationsAsync(page);
        }

        UpdateAnnotateButtons();
        if (CommentsPanel.Visibility == Visibility.Visible)
        {
            await LoadCommentsAsync();
        }
    }

    private void InvalidateThumbnail(int page)
    {
        _thumbnailCache.RemoveWhere(k => k.Page == page);
        if ((uint)page < (uint)_thumbnails.Count)
        {
            var item = _thumbnails[page];
            item.Image = null;
            _ = LoadThumbnailAsync(item);
        }
    }

    private async Task PreloadAnnotationsAsync()
    {
        if (_document is not { } document)
        {
            return;
        }

        int current = Viewer.CurrentPage;
        for (int page = Math.Max(0, current - 2); page <= Math.Min(document.PageCount - 1, current + 2); page++)
        {
            if (!_annotationCache.ContainsKey(page))
            {
                _annotationCache[page] = await document.GetAnnotationsAsync(page);
            }
        }
    }

    private void UpdateAnnotateButtons()
    {
        AnnotateUndo.IsEnabled = _annotationUndo.Count > 0;
        AnnotateRedo.IsEnabled = _annotationRedo.Count > 0;
        AnnotateSave.IsEnabled = _document is { HasAnnotationChanges: true };
    }

    private void OnAnnotateUndo(object sender, RoutedEventArgs e) => Guard.Run(UndoAnnotationAsync, "Undo");

    private void OnAnnotateRedo(object sender, RoutedEventArgs e) => Guard.Run(RedoAnnotationAsync, "Redo");

    private void OnAnnotateSave(object sender, RoutedEventArgs e) => Guard.Run(() => SaveAnnotationsAsync(), "Saving");

    // ================================================================= saving

    /// <summary>Saves annotations into the file and reopens it. Returns false if it couldn't.</summary>
    private async Task<bool> SaveAnnotationsAsync(bool reload = true)
    {
        if (_document is not { } document)
        {
            return false;
        }

        AnnotateSave.IsEnabled = false;
        try
        {
            await document.SaveAsync(FilePath);
        }
        catch (Exception ex) when (ex is PdfEditException or IOException or UnauthorizedAccessException)
        {
            await EditErrors.ShowAsync(ex, XamlRoot);
            UpdateAnnotateButtons();
            return false;
        }

        _annotationUndo.Clear();
        _annotationRedo.Clear();
        _annotationCache.Clear();
        if (reload)
        {
            await ReloadAsync();
            await PreloadAnnotationsAsync(); // the Select tool picks from this list
            Toaster.Show("Saved", "");
            if (CommentsPanel.Visibility == Visibility.Visible)
            {
                await LoadCommentsAsync();
            }
        }

        UpdateAnnotateButtons();
        return true;
    }

    /// <summary>Page tools work on the saved file, so annotations must reach it first. Returns false to stay put.</summary>
    private async Task<bool> SaveAnnotationsFirstAsync(string action)
    {
        if (_document is not { HasAnnotationChanges: true })
        {
            return true;
        }

        bool save = await Dialogs.ConfirmAsync(XamlRoot, "Save your annotations first?", $"{action} works on the saved file, so your annotations need saving first.", "Save", "Cancel");
        return save && await SaveAnnotationsAsync();
    }

    /// <summary>Asks about unsaved annotations when closing. Returns false to keep the tab open.</summary>
    private async Task<bool> ConfirmCloseAnnotationsAsync()
    {
        if (_document is not { HasAnnotationChanges: true } || XamlRoot == null)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            Title = "Save your annotations?",
            Content = Dialogs.Wrap($"Your annotations on \"{FileName}\" haven't been saved yet."),
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
        };
        return await Dialogs.ShowAsync(dialog, XamlRoot) switch
        {
            ContentDialogResult.Primary => await SaveAnnotationsAsync(reload: false),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async Task<string?> AskForWordsAsync(string title, string current)
    {
        if (XamlRoot == null)
        {
            return null;
        }

        var box = new TextBox { Text = current, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, Width = 360 };
        var dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = "OK", CloseButtonText = "Cancel" };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        return await Dialogs.ShowAsync(dialog, XamlRoot) == ContentDialogResult.Primary ? box.Text : null;
    }

    // ================================================================= comments list

    private async Task LoadCommentsAsync()
    {
        if (_document is not { } document)
        {
            return;
        }

        var found = new List<CommentItem>();
        for (int page = 0; page < document.PageCount; page++)
        {
            var infos = await document.GetAnnotationsAsync(page, PdfWorkPriority.Background);
            _annotationCache[page] = infos;
            found.AddRange(infos.OrderBy(i => i.Bounds.Y).Select(i => new CommentItem(i))); // reading order
        }

        if (document != _document)
        {
            return;
        }

        _comments.Clear();
        foreach (var item in found)
        {
            _comments.Add(item);
        }

        NoCommentsText.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCommentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommentItem item)
        {
            Viewer.GoToPage(item.Info.PageIndex, Math.Max(0, item.Info.Bounds.Y - 0.05));
        }
    }

    private void OnCommentDelete(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is CommentItem item)
        {
            Guard.Run(() => DeleteAsync(item.Info), "Deleting");
        }
    }

    private void OnCommentEdit(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is CommentItem item)
        {
            Guard.Run(() => EditCommentAsync(item.Info), "Editing");
        }
    }

    private async Task EditCommentAsync(PdfAnnotationInfo info)
    {
        if (_document is not { } document || await AskForWordsAsync("Comment", info.Text ?? string.Empty) is not { } text)
        {
            return;
        }

        string old = info.Text ?? string.Empty;
        await document.SetAnnotationTextAsync(info.PageIndex, info.Name, text);
        await AfterAnnotationChangeAsync(info.PageIndex);
        Remember(
            async () => { await document.SetAnnotationTextAsync(info.PageIndex, info.Name, old); await AfterAnnotationChangeAsync(info.PageIndex); },
            async () => { await document.SetAnnotationTextAsync(info.PageIndex, info.Name, text); await AfterAnnotationChangeAsync(info.PageIndex); });
    }
}
