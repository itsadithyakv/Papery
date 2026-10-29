namespace Papery.Commands;

/// <summary>
/// One action the user can run: shown in the command palette (Ctrl+K), and the single place
/// where a tool's name, icon, shortcut and Pro status are defined.
/// </summary>
/// <param name="Glyph">Segoe Fluent Icons code point, e.g. "".</param>
/// <param name="Keywords">Extra words to search by ("shrink" finds Compress).</param>
internal sealed record AppCommand(
    string Title,
    string Glyph,
    Action Execute,
    string? Shortcut = null,
    string Keywords = "",
    Func<bool>? CanExecute = null)
{
    public bool IsAvailable => CanExecute?.Invoke() ?? true;

    /// <summary>Simple fuzzy match: every typed word must appear in the title or keywords.</summary>
    public bool Matches(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
             .All(word => Title.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                       || Keywords.Contains(word, StringComparison.CurrentCultureIgnoreCase));
}
