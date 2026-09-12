// Exercises the REAL compiled bridge HTTP/queue/cache methods without starting
// Unity. Allocate without MonoBehaviour's native constructor, inject snapshots,
// and complete queued commands as a test driver. This is not game validation.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class BridgeHttpTests
{
    private static Type BridgeType;
    private static object Bridge;
    private static string Url;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object Get(string name) { return BridgeType.GetField(name, Private).GetValue(Bridge); }
    private static void Set(string name, object value) { BridgeType.GetField(name, Private).SetValue(Bridge, value); }
    private static object Call(string name, params object[] args) { return BridgeType.GetMethod(name, Private).Invoke(Bridge, args); }

    private static string Request(string path, string body, int timeoutMs)
    {
        var request = (HttpWebRequest)WebRequest.Create(Url + path);
        request.Proxy = null;
        request.Timeout = 3000;
        request.ReadWriteTimeout = 3000;
        request.Headers["X-KSP-MCP-Token"] = "test-token";
        request.Headers["X-KSP-MCP-Timeout-Ms"] = timeoutMs.ToString();
        if (body != null)
        {
            request.Method = "POST";
            request.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            request.ContentLength = bytes.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
    }

    private static int Available(Semaphore semaphore)
    {
        int count = 0;
        while (semaphore.WaitOne(0)) count++;
        if (count > 0) semaphore.Release(count);
        return count;
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.ElapsedMilliseconds > 2000) throw new Exception(message);
            Thread.Sleep(1);
        }
    }

    private static object TakeQueued()
    {
        object pending = Get("_requests");
        object item = null;
        WaitUntil(delegate
        {
            lock (Get("_queueLock"))
            {
                if (((ICollection)pending).Count == 0) return false;
                item = pending.GetType().GetMethod("Dequeue").Invoke(pending, null);
                return true;
            }
        }, "command never reached Unity queue");
        return item;
    }

    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            string path = Path.Combine(args[1], new AssemblyName(eventArgs.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        ServicePointManager.DefaultConnectionLimit = 32;
        BridgeType = Assembly.LoadFrom(args[0]).GetType("KspMcp.KspMcpBridge");
        Bridge = FormatterServices.GetUninitializedObject(BridgeType);
        Set("_queueLock", new object());
        Set("_telemetryLock", new object());
        Set("_requests", Activator.CreateInstance(BridgeType.GetField("_requests", Private).FieldType));
        Set("_events", Activator.CreateInstance(BridgeType.GetField("_events", Private).FieldType, new object[] { 2048 }));
        Set("_commandWorkers", new Semaphore(8, 8));
        Set("_telemetryWorkers", new Semaphore(4, 4));
        Set("_longPollWorkers", new Semaphore(2, 2));
        Set("_host", "127.0.0.1");
        Set("_token", "test-token");
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        Set("_port", number);
        Url = "http://127.0.0.1:" + number;
        Set("_telemetryCache", new Dictionary<string, object> { { "sequence", 1L }, { "scene", "EDITOR" }, { "flight", new Dictionary<string, object>() } });
        Call("StartHttpServer");
        try
        {
            var wait = Task.Factory.StartNew(() => Request("/api/v1/telemetry?since=0&wait_ms=1000", null, 1000));
            WaitUntil(() => Available((Semaphore)Get("_longPollWorkers")) == 1, "long poll never started");
            string compact = Request("/api/v1/telemetry?sections=", null, 1000);
            if (compact.Contains("\"flight\"") || !compact.Contains("\"sequence\":1")) throw new Exception("projection");
            var command = Task.Factory.StartNew(() => Request("/api/v1/command", "{\"command\":\"flight.guidance_stop\"}", 1000));
            object pending = TakeQueued();
            Type pendingType = pending.GetType();
            lock (pendingType.GetField("Sync").GetValue(pending))
            {
                pendingType.GetField("Response").SetValue(pending, Encoding.UTF8.GetBytes("{\"ok\":true,\"result\":{\"stopped\":true}}"));
                pendingType.GetField("Completed").SetValue(pending, true);
                Monitor.PulseAll(pendingType.GetField("Sync").GetValue(pending));
            }
            if (!command.Wait(500) || !command.Result.Contains("stopped") || wait.IsCompleted)
                throw new Exception("long poll blocked command response");
            lock (Get("_telemetryLock"))
            {
                Set("_telemetryCache", new Dictionary<string, object> { { "sequence", 2L }, { "scene", "FLIGHT" } });
                Set("_eventSequence", 1L);
                Get("_events").GetType().GetMethod("Add").Invoke(Get("_events"), new object[] { new Dictionary<string, object> { { "event_id", 1L }, { "type", "test" } } });
                Monitor.PulseAll(Get("_telemetryLock"));
            }
            if (!wait.Wait(1000) || !wait.Result.Contains("\"sequence\":2")) throw new Exception("stale snapshot after wait");
            try
            {
                Request("/api/v1/command", "{\"command\":\"flight.stage\"}", 100);
                throw new Exception("unserviced command did not expire");
            }
            catch (WebException exception)
            {
                using (var response = (HttpWebResponse)exception.Response)
                    if (response == null || (int)response.StatusCode != 504) throw;
            }
            object expired = TakeQueued();
            if (!(bool)expired.GetType().GetField("Cancelled").GetValue(expired)) throw new Exception("expired command not cancelled");
            Console.WriteLine("compiled bridge HTTP: concurrent control, projection, fresh wakeup, queued timeout passed");
        }
        finally { Call("StopHttpServer"); }
    }
}
