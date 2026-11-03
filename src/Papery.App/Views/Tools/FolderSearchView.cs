using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Papery.Pdf;
using Papery.Services;

namespace Papery.Views.Tools;

/// <summary>A result picked from a folder search, with the words that found it.</summary>
internal sealed record FolderSearchPick(FolderSearchHit Hit, string Query);

/// <summary>Searches every PDF in a folder. Results appear as each file is read; picking one opens that page.</summary>
internal sealed partial class FolderSearchView : Grid
{
    // Thousands of result rows would only be scrolled past; the count says how many more there are.
    private const int MostShown = 500;

    private readonly Func<nint> _window;
    private readonly TextBlock _folderText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _query = new() { PlaceholderText = "Words to find", IsSpellCheckEnabled = false, MinWidth = 320 };
    private readonly Button _go = new() { Content = "Search" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _results = new() { Spacing = 4 };
    private string? _folder;
    private CancellationTokenSource? _searching;

    public FolderSearchView(Func<nint> window)
    {
        _window = window;
        Background = (Brush)Application.Current.Resources["PaperySurfaceBrush"];
        _status.Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"];
        _go.Style = (Style)Application.Current.Resources["PaperyPrimaryButtonStyle"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_query, "Words to find");

        var page = new StackPanel { MaxWidth = 900, Padding = new Thickness(32, 28, 32, 40), Spacing = 14 };
        page.Children.Add(new TextBlock { Text = "Search a folder", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });

        var choose = new Button { Content = "Choose a folder" };
        choose.Click += (_, _) => Guard.Run(ChooseFolderAsync, "Choosing a folder");
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        folderRow.Children.Add(choose);
        folderRow.Children.Add(_folderText);
        page.Children.Add(folderRow);

        var queryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        queryRow.Children.Add(_query);
        queryRow.Children.Add(_go);
        page.Children.Add(queryRow);
        page.Children.Add(_status);
        page.Children.Add(_results);

        _go.Click += (_, _) => Guard.Run(SearchOrStopAsync, "Searching");
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && _searching == null)
            {
                e.Handled = true;
                Guard.Run(SearchOrStopAsync, "Searching");
            }
        };

        Children.Add(new ScrollViewer { Content = page });
        Loaded += (_, _) => _query.Focus(FocusState.Programmatic);
        ShowFolder();
    }

    /// <summary>Raised when a result is picked; the window opens the file at that page.</summary>
    public event EventHandler<FolderSearchPick>? Picked;

    public void Stop() => _searching?.Cancel();

    private async Task ChooseFolderAsync()
    {
        if (await Pickers.PickFolderAsync(_window()) is { } folder)
        {
            _folder = folder;
            ShowFolder();
            _query.Focus(FocusState.Programmatic);
        }
    }

    private void ShowFolder() => _folderText.Text = _folder ?? "No folder chosen yet";

    private async Task SearchOrStopAsync()
    {
        if (_searching != null)
        {
            _searching.Cancel();
            return;
        }

        string query = _query.Text.Trim();
        if (query.Length == 0)
        {
            _query.Focus(FocusState.Programmatic);
            return;
        }

        if (_folder == null)
        {
            await ChooseFolderAsync();
            if (_folder == null)
            {
                return;
            }
        }

        _results.Children.Clear();
        using var cts = new CancellationTokenSource();
        _searching = cts;
        _go.Content = "Stop";
        int found = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FolderSearchProgress? last = null;
        var progress = new Progress<FolderSearchProgress>(p =>
        {
            last = p;
            if (_searching != null)
            {
                _status.Text = $"Reading file {Math.Min(p.FilesDone + 1, p.FilesTotal):N0} of {p.FilesTotal:N0}… {Found(found, files.Count)}";
            }
        });

        try
        {
            await foreach (var hit in PdfEngine.Shared.SearchFolderAsync(_folder, query, progress, cts.Token))
            {
                found++;
                files.Add(hit.Path);
                if (found <= MostShown)
                {
                    _results.Children.Add(Row(hit, query));
                }
            }

            _status.Text = Summary(found, files.Count, last);
        }
        catch (OperationCanceledException)
        {
            _status.Text = $"Stopped. {Found(found, files.Count)}";
        }
        finally
        {
            _searching = null;
            _go.Content = "Search";
        }
    }

    private static string Found(int found, int files) =>
        found == 0 ? "Nothing found yet." : $"{found:N0} {(found == 1 ? "match" : "matches")} in {files:N0} {(files == 1 ? "file" : "files")}.";

    private static string Summary(int found, int files, FolderSearchProgress? last)
    {
        string text = found == 0 ? $"No matches in {last?.FilesTotal ?? 0:N0} PDFs." : Found(found, files);
        if (found > MostShown)
        {
            text += $" Showing the first {MostShown:N0}.";
        }

        if (last is { Locked: > 0 } p)
        {
            text += p.Locked == 1 ? " 1 file has a password, so it wasn't searched." : $" {p.Locked:N0} files have passwords, so they weren't searched.";
        }

        return text;
    }

    /// <summary>The file and page above a line of the text, with the found words in bold.</summary>
    private Button Row(FolderSearchHit hit, string query)
    {
        var where = new TextBlock { Style = (Style)Application.Current.Resources["PaperySecondaryTextStyle"], FontSize = 12 };
        where.Text = $"{Path.GetFileName(hit.Path)} · page {hit.PageIndex + 1}";
        var snippet = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
        snippet.Inlines.Add(new Run { Text = "…" + hit.Before.TrimStart() });
        snippet.Inlines.Add(new Run { Text = hit.Match, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["PaperyCoralBrush"] });
        snippet.Inlines.Add(new Run { Text = hit.After.TrimEnd() + "…" });
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(where);
        content.Children.Add(snippet);

        var row = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8),
            Style = (Style)Application.Current.Resources["PaperyQuietButtonStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{Path.GetFileName(hit.Path)}, page {hit.PageIndex + 1}: {hit.Before}{hit.Match}{hit.After}");
        ToolTipService.SetToolTip(row, hit.Path);
        row.Click += (_, _) => Picked?.Invoke(this, new FolderSearchPick(hit, query));
        return row;
    }
}
