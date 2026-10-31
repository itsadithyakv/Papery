namespace Papery.Pdf.Editing;

/// <summary>One bookmark as a row in a flat, indented list.</summary>
/// <param name="Top">Where on the page it lands, as a share of the page's height from the top.</param>
/// <param name="Uri">A web address, for a bookmark that opens one instead of going to a page.</param>
public sealed record OutlineRow(string Title, int? PageIndex, int Depth, double? Top = null, string? Uri = null);

/// <summary>
/// Edits bookmarks as an indented list, which is far easier to keep valid than a tree. A row's
/// "family" is the row and everything indented beneath it; moving a row brings its family along.
/// </summary>
public sealed class OutlineEditor
{
    private readonly List<OutlineRow> _rows = [];

    public OutlineEditor(IReadOnlyList<PdfOutlineItem> outline) => Flatten(outline, 0);

    public IReadOnlyList<OutlineRow> Rows => _rows;

    /// <summary>Adds a bookmark after a row, as its sibling (at the end when <paramref name="after"/> is negative). Returns where.</summary>
    public int Add(int after, string title, int page)
    {
        if (after < 0 || after >= _rows.Count)
        {
            _rows.Add(new OutlineRow(title, page, 0));
            return _rows.Count - 1;
        }

        int at = after + FamilySize(after);
        _rows.Insert(at, new OutlineRow(title, page, _rows[after].Depth));
        return at;
    }

    public void Rename(int index, string title) => _rows[index] = _rows[index] with { Title = title };

    public void SetPage(int index, int page) => _rows[index] = _rows[index] with { PageIndex = page, Top = null, Uri = null };

    /// <summary>Removes a bookmark and everything under it.</summary>
    public void Remove(int index) => _rows.RemoveRange(index, FamilySize(index));

    /// <summary>Swaps a row's family with the sibling before it. Returns the row's new index.</summary>
    public int MoveUp(int index)
    {
        int depth = _rows[index].Depth;
        int previous = index - 1;
        while (previous >= 0 && _rows[previous].Depth > depth)
        {
            previous--;
        }

        if (previous < 0 || _rows[previous].Depth < depth)
        {
            return index; // it's the first of its siblings
        }

        var family = _rows.GetRange(index, FamilySize(index));
        _rows.RemoveRange(index, family.Count);
        _rows.InsertRange(previous, family);
        return previous;
    }

    /// <summary>Swaps a row's family with the sibling after it. Returns the row's new index.</summary>
    public int MoveDown(int index)
    {
        int size = FamilySize(index);
        int next = index + size;
        if (next >= _rows.Count || _rows[next].Depth != _rows[index].Depth)
        {
            return index; // it's the last of its siblings
        }

        int nextSize = FamilySize(next);
        var family = _rows.GetRange(index, size);
        _rows.RemoveRange(index, size);
        _rows.InsertRange(index + nextSize, family);
        return index + nextSize;
    }

    /// <summary>Makes a row a child of the sibling before it.</summary>
    public void Indent(int index)
    {
        if (index > 0 && _rows[index - 1].Depth >= _rows[index].Depth)
        {
            Shift(index, 1);
        }
    }

    /// <summary>Lifts a row up a level. Rows after it at the old level stay where they are.</summary>
    public void Outdent(int index)
    {
        if (_rows[index].Depth > 0)
        {
            Shift(index, -1);
        }
    }

    /// <summary>The bookmarks as the tree a PDF stores.</summary>
    public IReadOnlyList<PdfOutlineItem> ToTree()
    {
        int position = 0;
        return Read(0, ref position, null);
    }

    /// <summary>A bookmark with no page of its own uses its parent's, or it would vanish on saving.</summary>
    private List<PdfOutlineItem> Read(int depth, ref int position, int? parentPage)
    {
        var level = new List<PdfOutlineItem>();
        while (position < _rows.Count && _rows[position].Depth >= depth)
        {
            var row = _rows[position++];
            int? page = row.Uri != null ? row.PageIndex : row.PageIndex ?? parentPage;
            var children = Read(row.Depth + 1, ref position, page ?? parentPage);
            level.Add(new PdfOutlineItem(row.Title, page ?? (children.Count == 0 && row.Uri == null ? 0 : null), children, row.Top, row.Uri));
        }

        return level;
    }

    private void Shift(int index, int by)
    {
        int size = FamilySize(index);
        for (int i = index; i < index + size; i++)
        {
            _rows[i] = _rows[i] with { Depth = _rows[i].Depth + by };
        }
    }

    private int FamilySize(int index)
    {
        int end = index + 1;
        while (end < _rows.Count && _rows[end].Depth > _rows[index].Depth)
        {
            end++;
        }

        return end - index;
    }

    private void Flatten(IReadOnlyList<PdfOutlineItem> items, int depth)
    {
        foreach (var item in items)
        {
            _rows.Add(new OutlineRow(item.Title, item.PageIndex, depth, item.Top, item.Uri));
            Flatten(item.Children, depth + 1);
        }
    }
}
