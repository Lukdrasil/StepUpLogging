namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The least-recently-used order of the traces of one <see cref="PreErrorBufferSink"/> stripe. It holds no buffers and
/// no lock of its own to take: the caller locks <see cref="Gate"/> around <see cref="Touch"/> and <see cref="Clear"/>.
/// </summary>
internal sealed class TraceLruStripe(int capacity)
{
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();

    /// <summary>The lock that serialises every touch, eviction and buffer change of this stripe's traces.</summary>
    internal object Gate { get; } = new();

    /// <summary>
    /// Marks <paramref name="key"/> as the most recently used trace. Returns the one least recently used trace that
    /// no longer fits the capacity, or <see langword="null"/> when nothing was displaced.
    /// </summary>
    internal string? Touch(string key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
        else
        {
            _nodes[key] = _order.AddFirst(key);
        }

        return EvictOverflow();
    }

    /// <summary>Forgets every trace.</summary>
    internal void Clear()
    {
        _order.Clear();
        _nodes.Clear();
    }

    private string? EvictOverflow()
    {
        if (_order.Count <= capacity || _order.Last is not { } oldest)
        {
            return null;
        }

        _order.RemoveLast();
        _nodes.Remove(oldest.Value);
        return oldest.Value;
    }
}
