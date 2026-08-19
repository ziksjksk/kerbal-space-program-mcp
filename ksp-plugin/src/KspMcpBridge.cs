using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Collections.Specialized;
using UnityEngine;

namespace KspMcp
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class KspMcpBridge : MonoBehaviour
    {
        public static KspMcpBridge Instance;

        private HttpListener _listener;
        private Thread _listenerThread;
        private readonly object _queueLock = new object();
        private readonly Queue<PendingRequest> _requests = new Queue<PendingRequest>();
        private KspMcpCraft _craft;
        private KspMcpFlight _flight;
        private string _host = "127.0.0.1";
        private int _port = 8765;
        private string _token = "";
        private int _maxRequestsPerFrame = 8;
        private bool _stopping;
        private float _lastTelemetryAt = -1f;
        private long _telemetrySequence;
        private long _eventSequence;
        private readonly List<Dictionary<string, object>> _events = new List<Dictionary<string, object>>();
        private Dictionary<string, object> _telemetryCache;
        private const int MaxTelemetryEvents = 256;

        private sealed class PendingRequest
        {
            public HttpListenerContext Context;
            public string Command;
            public Dictionary<string, object> Args;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            LoadConfig();
            _craft = new KspMcpCraft();
            _flight = new KspMcpFlight(_craft);
            _flight.Start();
            StartHttpServer();
            UpdateTelemetryCache(true);
            Log("started; endpoint http://" + _host + ":" + _port + "/api/v1");
        }

        private void Update()
        {
            _craft.Tick();
            _flight.Tick();
            UpdateTelemetryCache(false);

            for (int index = 0; index < _maxRequestsPerFrame; index++)
            {
                PendingRequest request = DequeueRequest();
                if (request == null) break;

                Dictionary<string, object> envelope;
                try
                {
                    object result = Dispatch(request.Command, request.Args);
                    envelope = Success(result);
                    RecordEvent("command.completed", new Dictionary<string, object> { { "command", request.Command } });
                }
                catch (KspMcpException exception)
                {
                    envelope = Failure(exception.Code, exception.Message, exception.Details);
                    RecordEvent("command.failed", new Dictionary<string, object> { { "command", request.Command }, { "code", exception.Code } });
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    envelope = Failure("game_exception", exception.Message, null);
                    RecordEvent("command.failed", new Dictionary<string, object> { { "command", request.Command }, { "code", "game_exception" } });
                }
                WriteResponse(request.Context, envelope, 200);
            }
            UpdateTelemetryCache(true);
        }

        private PendingRequest DequeueRequest()
        {
            lock (_queueLock)
            {
                return _requests.Count == 0 ? null : _requests.Dequeue();
            }
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KspMcp/PluginData/config.cfg");
                if (!File.Exists(path)) return;
                ConfigNode root = ConfigNode.Load(path);
                ConfigNode node = root == null ? null : root.GetNode("KSP_MCP");
                if (node == null) return;

                string host = node.GetValue("host");
                int port;
                if (!string.IsNullOrEmpty(host)) _host = host.Trim();
                if (int.TryParse(node.GetValue("port"), out port) && port > 0 && port < 65536) _port = port;
                string token = node.GetValue("token");
                if (token != null) _token = token.Trim();
                int maxRequests;
                if (int.TryParse(node.GetValue("maxRequestsPerFrame"), out maxRequests) && maxRequests > 0)
                {
                    _maxRequestsPerFrame = Math.Min(maxRequests, 32);
                }
            }
            catch (Exception exception)
            {
                Log("could not read config: " + exception.Message);
            }
        }

        private void StartHttpServer()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://" + _host + ":" + _port + "/");
                _listener.Start();
                _listenerThread = new Thread(ListenLoop);
                _listenerThread.IsBackground = true;
                _listenerThread.Name = "KspMcpHttpListener";
                _listenerThread.Start();
            }
            catch (Exception exception)
            {
                _listener = null;
                Log("could not start HTTP bridge: " + exception.Message);
            }
        }

        private void ListenLoop()
        {
            while (!_stopping && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context = null;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    if (!_stopping) Log("HTTP listener stopped unexpectedly");
                    break;
                }

                if (context == null) continue;
                if (!IsAuthorized(context.Request))
                {
                    WriteResponse(context, Failure("unauthorized", "invalid or missing X-KSP-MCP-Token", null), 401);
                    continue;
                }

                string path = context.Request.Url == null ? "" : context.Request.Url.AbsolutePath.TrimEnd('/');
                if (string.IsNullOrEmpty(path)) path = "/";

                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    (path == "/api/v1/status" || path == "/api/v1/health"))
                {
                    Enqueue(context, "status", new Dictionary<string, object>());
                    continue;
                }
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/api/v1/telemetry")
                {
                    Enqueue(context, "telemetry", QueryArguments(context.Request));
                    continue;
                }
                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/api/v1/parts")
                {
                    Enqueue(context, "parts.list", new Dictionary<string, object>());
                    continue;
                }
                if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) || path != "/api/v1/command")
                {
                    WriteResponse(context, Failure("not_found", "endpoint not found", path), 404);
                    continue;
                }

                try
                {
                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }
                    if (body.Length > 8 * 1024 * 1024)
                    {
                        WriteResponse(context, Failure("payload_too_large", "command payload exceeds 8 MiB", null), 413);
                        continue;
                    }

                    Dictionary<string, object> commandObject = JsonUtil.Object(McpJson.Deserialize(body));
                    if (commandObject == null)
                    {
                        WriteResponse(context, Failure("invalid_request", "request body must be a JSON object", null), 400);
                        continue;
                    }
                    string command = JsonUtil.String(commandObject, "command", null);
                    Dictionary<string, object> args = JsonUtil.Object(JsonUtil.Get(commandObject, "args"));
                    if (string.IsNullOrEmpty(command))
                    {
                        WriteResponse(context, Failure("invalid_request", "request requires command", null), 400);
                        continue;
                    }
                    Enqueue(context, command, args ?? new Dictionary<string, object>());
                }
                catch (Exception exception)
                {
                    WriteResponse(context, Failure("invalid_json", exception.Message, null), 400);
                }
            }
        }

        private void Enqueue(HttpListenerContext context, string command, Dictionary<string, object> args)
        {
            lock (_queueLock)
            {
                if (_requests.Count >= 64)
                {
                    WriteResponse(context, Failure("busy", "too many queued KSP commands", null), 429);
                    return;
                }
                _requests.Enqueue(new PendingRequest { Context = context, Command = command, Args = args });
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            if (string.IsNullOrEmpty(_token)) return true;
            return string.Equals(request.Headers["X-KSP-MCP-Token"], _token, StringComparison.Ordinal);
        }

        private static void WriteResponse(HttpListenerContext context, Dictionary<string, object> response, int statusCode)
        {
            if (context == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(McpJson.Serialize(response));
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.ContentLength64 = bytes.Length;
                using (Stream stream = context.Response.OutputStream)
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception exception)
            {
                Debug.Log("[KspMcp] could not write HTTP response: " + exception.Message);
            }
        }

        public object Dispatch(string command, Dictionary<string, object> args)
        {
            switch (command)
            {
                case "status": return Status();
                case "telemetry": return Telemetry(args);
                case "batch": return Batch(args);
                case "parts.list": return _craft.ListAvailableParts(args);
                case "editor.new": return _craft.NewCraft(args);
                case "editor.snapshot": return _craft.Snapshot();
                case "editor.apply_craft": return _craft.ApplyCraft(args);
                case "editor.add_part": return _craft.AddPart(args);
                case "editor.attach_part": return _craft.AttachPart(args);
                case "editor.update_part": return _craft.UpdatePart(args);
                case "editor.remove_part": return _craft.RemovePart(args);
                case "editor.set_stage": return _craft.SetStage(args);
                case "editor.set_action_group": return _craft.SetActionGroup(args);
                case "editor.validate": return _craft.Validate();
                case "editor.analyze": return _craft.Analyze(args);
                case "editor.save": return _craft.Save(args);
                case "editor.load": return _craft.Load(args);
                case "editor.clear": return _craft.Clear();
                case "editor.launch": return _craft.Launch(args);
                case "editor.job_status": return _craft.JobStatus(args);
                case "flight.state": return _flight.Snapshot();
                case "flight.guidance_start": return _flight.StartGuidance(args);
                case "flight.guidance_stop": return _flight.StopGuidance();
                case "flight.guidance_status": return _flight.GuidanceStatus();
                case "flight.stage": return _flight.Stage();
                case "flight.set_controls": return _flight.SetControls(args);
                case "flight.set_sas": return _flight.SetSas(args);
                case "flight.set_rcs": return _flight.SetRcs(args);
                case "flight.warp": return _flight.Warp(args);
                case "flight.activate_part": return _flight.ActivatePart(args);
                case "flight.abort": return _flight.Abort();
                case "flight.clear_control": return _flight.ClearControl();
                case "flight.recover": return _flight.Recover();
                default: throw new KspMcpException("unknown_command", "unknown command: " + command, null);
            }
        }

        private object Batch(Dictionary<string, object> args)
        {
            List<object> commands = JsonUtil.Array(args, "commands");
            if (commands == null || commands.Count == 0) throw new KspMcpException("invalid_batch", "batch requires at least one command", null);
            if (commands.Count > 32) throw new KspMcpException("invalid_batch", "batch accepts at most 32 commands", null);

            var results = new List<object>();
            foreach (object raw in commands)
            {
                Dictionary<string, object> item = JsonUtil.Object(raw);
                if (item == null) throw new KspMcpException("invalid_batch", "each batch item must be an object", null);
                string command = JsonUtil.RequiredString(item, "command");
                if (command == "batch" || command == "editor.launch" || command == "flight.abort" || command == "flight.recover")
                {
                    throw new KspMcpException("unsafe_batch_command", command + " must use its dedicated command", null);
                }
                Dictionary<string, object> commandArgs = JsonUtil.Object(JsonUtil.Get(item, "args")) ?? new Dictionary<string, object>();
                try
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "command", command },
                        { "ok", true },
                        { "result", Dispatch(command, commandArgs) }
                    });
                }
                catch (KspMcpException exception)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "command", command },
                        { "ok", false },
                        { "error", new Dictionary<string, object> { { "code", exception.Code }, { "message", exception.Message } } }
                    });
                }
            }
            return new Dictionary<string, object> { { "count", results.Count }, { "results", results } };
        }

        private Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>
            {
                { "bridge", "ksp-mcp" },
                { "bridge_version", "0.2.0" },
                { "scene", SceneName() },
                { "endpoint", "http://" + _host + ":" + _port },
                { "editor", _craft.Status() },
                { "flight", _flight.Snapshot() },
                { "capabilities", new Dictionary<string, object>
                    {
                        { "editor", new List<object> { "new", "snapshot", "apply", "add", "attach", "update", "remove", "stage", "action_group", "validate", "analyze", "save", "load", "launch", "job_status" } },
                        { "flight", new List<object> { "state", "compact_telemetry", "guidance_start", "guidance_stop", "guidance_status", "stage", "controls", "sas", "rcs", "warp", "activate_part", "abort", "recover" } },
                        { "bridge", new List<object> { "telemetry", "batch" } }
                    }
                }
            };
        }

        private Dictionary<string, object> Telemetry(Dictionary<string, object> args)
        {
            UpdateTelemetryCache(true);
            Dictionary<string, object> cache = _telemetryCache ?? new Dictionary<string, object>();
            long since = (long)Math.Max(0d, JsonUtil.Number(args, "since", 0d));
            int limit = Math.Max(1, Math.Min(256, JsonUtil.Integer(args, "limit", 64)));
            bool includeEvents = JsonUtil.Boolean(args, "include_events", true);
            var result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> item in cache) result[item.Key] = item.Value;
            result["event_cursor"] = _eventSequence;
            result["events"] = includeEvents ? EventsSince(since, limit) : new List<object>();
            return result;
        }

        private List<object> EventsSince(long since, int limit)
        {
            var result = new List<object>();
            for (int index = 0; index < _events.Count; index++)
            {
                Dictionary<string, object> item = _events[index];
                long eventId = (long)item["event_id"];
                if (eventId <= since) continue;
                result.Add(item);
                if (result.Count >= limit) break;
            }
            return result;
        }

        internal void RecordEvent(string type, object data)
        {
            _eventSequence++;
            _events.Add(new Dictionary<string, object>
            {
                { "event_id", _eventSequence },
                { "universal_time", SafeUniversalTime() },
                { "type", type },
                { "data", data }
            });
            if (_events.Count > MaxTelemetryEvents) _events.RemoveAt(0);
        }

        private void UpdateTelemetryCache(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && _lastTelemetryAt >= 0f && now - _lastTelemetryAt < 0.1f) return;
            _lastTelemetryAt = now;
            _telemetrySequence++;
            _telemetryCache = new Dictionary<string, object>
            {
                { "sequence", _telemetrySequence },
                { "captured_at", SafeUniversalTime() },
                { "scene", SceneName() },
                { "editor", _craft.Status() },
                { "flight", _flight.CompactSnapshot() }
            };
        }

        private static double SafeUniversalTime()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch (Exception) { return 0d; }
        }

        private static Dictionary<string, object> QueryArguments(HttpListenerRequest request)
        {
            var result = new Dictionary<string, object>();
            NameValueCollection query = request == null ? null : request.QueryString;
            if (query == null) return result;
            double number;
            if (double.TryParse(query["since"], out number)) result["since"] = number;
            int limit;
            if (int.TryParse(query["limit"], out limit)) result["limit"] = limit;
            bool includeEvents;
            if (bool.TryParse(query["include_events"], out includeEvents)) result["include_events"] = includeEvents;
            return result;
        }

        public static string SceneName()
        {
            try
            {
                return HighLogic.LoadedScene.ToString();
            }
            catch (Exception)
            {
                return Application.loadedLevelName;
            }
        }

        private void StopHttpServer()
        {
            _stopping = true;
            try
            {
                if (_listener != null) _listener.Stop();
                if (_listenerThread != null && _listenerThread.IsAlive) _listenerThread.Join(1000);
            }
            catch (Exception exception)
            {
                Log("error while stopping HTTP bridge: " + exception.Message);
            }
            finally
            {
                _listener = null;
                _listenerThread = null;
            }
        }

        private void OnDestroy()
        {
            StopHttpServer();
            if (_flight != null) _flight.Stop();
            if (Instance == this) Instance = null;
        }

        internal static Dictionary<string, object> Success(object result)
        {
            return new Dictionary<string, object> { { "ok", true }, { "result", result } };
        }

        internal static Dictionary<string, object> Failure(string code, string message, object details)
        {
            var error = new Dictionary<string, object> { { "code", code }, { "message", message } };
            if (details != null) error["details"] = details;
            return new Dictionary<string, object> { { "ok", false }, { "error", error } };
        }

        internal static void Log(string message)
        {
            Debug.Log("[KspMcp] " + message);
        }
    }

    public sealed class KspMcpException : Exception
    {
        public readonly string Code;
        public readonly object Details;

        public KspMcpException(string code, string message, object details)
            : base(message)
        {
            Code = code;
            Details = details;
        }
    }
}
