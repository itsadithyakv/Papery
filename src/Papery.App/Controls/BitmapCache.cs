using Microsoft.UI.Xaml.Media.Imaging;

namespace Papery.Controls;

/// <summary>
/// Least-recently-used cache of rendered page images, capped by memory rather than count,
/// so a 1,000-page document never holds more than the budget no matter how far you scroll.
/// UI thread only.
/// </summary>
internal sealed class BitmapCache<TKey>(long maxBytes)
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _items = new();
    private readonly LinkedList<Entry> _lru = new(); // most recent first
    private long _bytes;

    public long Bytes => _bytes;

    public bool TryGet(TKey key, out WriteableBitmap bitmap)
    {
        if (_items.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }

        bitmap = null!;
        return false;
    }

    /// <summary>Any cached entry matching <paramref name="predicate"/>, preferring the largest.</summary>
    public WriteableBitmap? FindBest(Func<TKey, bool> predicate) =>
        _lru.Where(e => predicate(e.Key)).OrderByDescending(e => e.Bytes).Select(e => e.Bitmap).FirstOrDefault();

    public void Add(TKey key, WriteableBitmap bitmap)
    {
        Remove(key);
        long bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
        _items[key] = _lru.AddFirst(new Entry(key, bitmap, bytes));
        _bytes += bytes;
        while (_bytes > maxBytes && _lru.Count > 1)
        {
            Remove(_lru.Last!.Value.Key);
        }
    }

    public void Remove(TKey key)
    {
        if (_items.Remove(key, out var node))
        {
            _lru.Remove(node);
            _bytes -= node.Value.Bytes;
        }
    }

    public void Clear()
    {
        _items.Clear();
        _lru.Clear();
        _bytes = 0;
    }

    private sealed record Entry(TKey Key, WriteableBitmap Bitmap, long Bytes);
}
