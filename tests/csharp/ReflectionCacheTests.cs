using System;
using KspMcp;

internal static class ReflectionCacheTests
{
    private class Base { public double Thrust = 5; }
    private class Engine : Base
    {
        public double thrust = 7;
        public bool Running { get; set; }
        private int Secret = 3;
        public int ReadSecret() { return Secret; }
    }

    public static void Run()
    {
        var first = new Engine();
        var second = new Engine();
        var upper = KspMcpReflectionCache.FindField(typeof(Engine), "Thrust");
        var lower = KspMcpReflectionCache.FindField(typeof(Engine), "thrust");
        if (upper.Name != "Thrust" || lower.Name != "thrust") throw new Exception("exact case precedence");
        upper.SetValue(first, 99d);
        if ((double)upper.GetValue(first) != 99d || (double)upper.GetValue(second) != 5d)
            throw new Exception("cached a value rather than metadata");
        if (KspMcpReflectionCache.FindField(typeof(Engine), "SECRET").Name != "Secret")
            throw new Exception("private fallback");
        var property = KspMcpReflectionCache.FindProperty(typeof(Engine), "running");
        property.SetValue(first, true, null);
        if (!(bool)property.GetValue(first, null)) throw new Exception("property cache");
        for (int i = 0; i < 10000; i++)
            if (KspMcpReflectionCache.FindField(typeof(Engine), "missing") != null ||
                KspMcpReflectionCache.FindProperty(typeof(Engine), "missing") != null)
                throw new Exception("missing member cache");
        Console.WriteLine("reflection cache: exact/fallback/private/inherited/fresh values/missing passed");
    }
}
