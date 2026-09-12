using System;
using System.Collections.Generic;

namespace KspMcp
{
    // Caller owns synchronization. Event IDs must be consecutive and payloads
    // must not be mutated after publication.
    internal sealed class KspMcpEventBuffer
    {
        private readonly Dictionary<string, object>[] _items;
        private int _start;
        public int Count { get; private set; }

        public KspMcpEventBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity");
            _items = new Dictionary<string, object>[capacity];
        }

        public long OldestCursor
        {
            get { return Count == 0 ? 0 : (long)_items[_start]["event_id"]; }
        }

        public void Add(Dictionary<string, object> item)
        {
            if (Count < _items.Length)
            {
                _items[(_start + Count) % _items.Length] = item;
                Count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }

        public List<object> Since(long since, int limit)
        {
            var result = new List<object>(Math.Max(0, Math.Min(limit, Count)));
            if (Count == 0 || limit <= 0) return result;
            long oldest = OldestCursor;
            if (since >= oldest + Count - 1) return result;
            int offset = since < oldest ? 0 : (int)(since - oldest + 1);
            for (int i = offset; i < Count && result.Count < limit; i++)
                result.Add(_items[(_start + i) % _items.Length]);
            return result;
        }
    }
}
