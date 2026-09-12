using System;
using System.Collections.Generic;
using KspMcp;

internal static class EventBufferTests
{
    private static void Main()
    {
        ReflectionCacheTests.Run();
        var ring = new KspMcpEventBuffer(7);
        var reference = new List<Dictionary<string, object>>();
        if (ring.Since(0, 3).Count != 0) throw new Exception("empty buffer");
        for (long id = 1; id <= 10000; id++)
        {
            var item = new Dictionary<string, object> { { "event_id", id }, { "type", "test" } };
            ring.Add(item);
            reference.Add(item);
            if (reference.Count > 7) reference.RemoveAt(0);
            if (ring.Count != reference.Count || ring.OldestCursor != (long)reference[0]["event_id"])
                throw new Exception("window mismatch");
            foreach (long since in new long[] { 0, Math.Max(0, id - 20), Math.Max(0, id - 4), id, id + 1, long.MaxValue })
            {
                foreach (int limit in new int[] { 0, 1, 3, 256 })
                {
                    var expected = new List<object>();
                    foreach (var old in reference)
                        if ((long)old["event_id"] > since && expected.Count < limit) expected.Add(old);
                    var actual = ring.Since(since, limit);
                    if (actual.Count != expected.Count) throw new Exception("count mismatch");
                    for (int index = 0; index < actual.Count; index++)
                        if (!object.ReferenceEquals(actual[index], expected[index])) throw new Exception("cursor mismatch");
                }
            }
        }
        Console.WriteLine("event ring: 240000 reference comparisons passed");
    }
}
