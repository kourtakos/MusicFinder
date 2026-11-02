namespace MusicFinder.App.Services.Artwork;

/// <summary>Small thread-safe least-recently-used cache; the oldest entry is evicted when capacity is exceeded.</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly object _gate = new();

    public LruCache(int capacity) => _capacity = Math.Max(1, capacity);

    public int Count { get { lock (_gate) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing)) _order.Remove(existing);
            var node = new LinkedListNode<(TKey, TValue)>((key, value));
            _order.AddFirst(node);
            _map[key] = node;
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
