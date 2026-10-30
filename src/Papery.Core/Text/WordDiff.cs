namespace Papery.Core.Text;

/// <summary>Which words differ between two lists. Pure, so Compare's logic can be tested without any PDF.</summary>
public static class WordDiff
{
    // A page rarely has this many words. Past it, memory for the table outweighs the benefit, so
    // the middle is treated as changed rather than risking a very long wait.
    private const int LargestTable = 16_000_000;

    /// <summary>Indexes of words only in <paramref name="left"/> (removed) and only in <paramref name="right"/> (added).</summary>
    public static (IReadOnlyList<int> Removed, IReadOnlyList<int> Added) Compare(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        // Matching ends are the common case, and make the table far smaller.
        int start = 0;
        while (start < left.Count && start < right.Count && left[start] == right[start])
        {
            start++;
        }

        int endLeft = left.Count, endRight = right.Count;
        while (endLeft > start && endRight > start && left[endLeft - 1] == right[endRight - 1])
        {
            endLeft--;
            endRight--;
        }

        var removed = new List<int>();
        var added = new List<int>();
        int n = endLeft - start, m = endRight - start;
        if (n == 0 || m == 0 || (long)(n + 1) * (m + 1) > LargestTable)
        {
            removed.AddRange(Enumerable.Range(start, n));
            added.AddRange(Enumerable.Range(start, m));
            return (removed, added);
        }

        // Longest common subsequence.
        int[,] table = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                table[i, j] = left[start + i] == right[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (left[start + a] == right[start + b])
            {
                a++;
                b++;
            }
            else if (table[a + 1, b] >= table[a, b + 1])
            {
                removed.Add(start + a++);
            }
            else
            {
                added.Add(start + b++);
            }
        }

        while (a < n)
        {
            removed.Add(start + a++);
        }

        while (b < m)
        {
            added.Add(start + b++);
        }

        return (removed, added);
    }
}
