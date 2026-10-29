namespace Papery.Pdf.Editing;

/// <summary>
/// The concrete PDFium steps that turn a <see cref="PdfComposeRequest"/> into a document.
/// Pure maths, so it is unit-tested without touching PDFium.
/// </summary>
/// <remarks>
/// Steps, in order:
/// 1. Start from the edited source (its pages are pool entries 0..N-1) or an empty document.
/// 2. Append <see cref="Imports"/> (one import per source keeps shared fonts and images single),
///    then <see cref="Blanks"/>.
/// 3. Delete <see cref="Deletions"/> (descending, so earlier indices stay valid).
/// 4. Reorder so result page k is current page <see cref="Order"/>[k] (null = already in order).
/// 5. Add <see cref="PageRef.QuarterTurns"/> of each result page.
/// </remarks>
internal sealed record ComposePlan(
    IReadOnlyList<ComposeImport> Imports,
    IReadOnlyList<PdfPageSize> Blanks,
    IReadOnlyList<int> Deletions,
    int[]? Order)
{
    public static ComposePlan Create(IReadOnlyList<PageRef> pages, int? inPlaceSource, int inPlacePageCount)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pages.Count, nameof(pages));
        int baseCount = inPlaceSource.HasValue ? inPlacePageCount : 0;

        // Which pool entry (page of the document being built) each result page will be.
        var poolOf = new int[pages.Count];
        var baseUsed = new bool[baseCount];
        var importsBySource = new Dictionary<int, List<(int ResultIndex, int Page)>>();
        var importOrder = new List<int>();
        var blanks = new List<(int ResultIndex, PdfPageSize Size)>();

        for (int k = 0; k < pages.Count; k++)
        {
            var page = pages[k];
            if (page.IsBlank)
            {
                blanks.Add((k, page.BlankSize));
            }
            else if (page.Source == inPlaceSource && page.Page < baseCount && !baseUsed[page.Page])
            {
                baseUsed[page.Page] = true; // first use of an existing page: keep it where it is
                poolOf[k] = page.Page;
            }
            else
            {
                // A duplicate, or a page from another file: copy it in.
                if (!importsBySource.TryGetValue(page.Source, out var list))
                {
                    importsBySource[page.Source] = list = [];
                    importOrder.Add(page.Source);
                }

                list.Add((k, page.Page));
            }
        }

        int next = baseCount;
        var imports = new List<ComposeImport>();
        foreach (int source in importOrder)
        {
            var list = importsBySource[source];
            imports.Add(new ComposeImport(source, list.Select(x => x.Page).ToArray()));
            foreach (var (resultIndex, _) in list)
            {
                poolOf[resultIndex] = next++;
            }
        }

        foreach (var (resultIndex, _) in blanks)
        {
            poolOf[resultIndex] = next++;
        }

        // Unused existing pages are deleted; later pool entries shift down by the deletions before them.
        var deletions = new List<int>();
        var shift = new int[next];
        int removed = 0;
        for (int i = 0; i < next; i++)
        {
            if (i < baseCount && !baseUsed[i])
            {
                deletions.Add(i);
                removed++;
            }

            shift[i] = removed;
        }

        deletions.Reverse();
        var order = new int[pages.Count];
        bool identity = true;
        for (int k = 0; k < pages.Count; k++)
        {
            order[k] = poolOf[k] - shift[poolOf[k]];
            identity &= order[k] == k;
        }

        return new ComposePlan(imports, blanks.Select(b => b.Size).ToArray(), deletions, identity ? null : order);
    }
}

/// <summary>Copy these pages of one source, in this order, onto the end of the document.</summary>
internal sealed record ComposeImport(int Source, int[] Pages);
