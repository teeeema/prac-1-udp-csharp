using UdpGame.Protocol;

namespace UdpGame.Server;

/// <summary>Bounded arrival-order cache; equality on UInt16 IDs handles rollover without ordering comparisons.</summary>
public sealed class RecentCommandWindow
{
    private readonly Dictionary<ushort, StateUpdate> _responses = [];
    private readonly Queue<ushort> _order = new();
    private readonly int _capacity;

    public RecentCommandWindow(int capacity = 1024)
    {
        if (capacity <= 0 || capacity > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _capacity = capacity;
    }

    public int Count => _responses.Count;
    public bool TryGet(ushort sequence, out StateUpdate response) => _responses.TryGetValue(sequence, out response);

    public void Remember(ushort sequence, StateUpdate response)
    {
        if (_responses.ContainsKey(sequence))
        {
            return; // A duplicate does not refresh or enlarge the window.
        }
        if (_responses.Count == _capacity)
        {
            _responses.Remove(_order.Dequeue());
        }
        _responses.Add(sequence, response);
        _order.Enqueue(sequence);
    }
}
