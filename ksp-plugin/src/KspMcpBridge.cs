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
        // Telemetry is a read-only snapshot produced on the Unity thread and
        // consumed by the HTTP listener thread. Keeping the cache and event
        // cursor behind one lock lets the no-visual endpoint answer without
        // waiting for the command queue or touching Unity objects off-thread.
        private readonly object _telemetryLock = new object();
        private readonly Queue<PendingRequest> _requests = new Queue<PendingRequest>();
        private KspMcpCraft _craft;
        private KspMcpFlight _flight;
        private string _host = "127.0.0.1";
        private int _port = 8765;
        private string _token = "";
        private int _maxRequestsPerFrame = 8;
        private bool _verboseLogging;
        private float _telemetryIntervalSeconds = 0.05f;
        private bool _stopping;
        private float _lastTelemetryAt = -1f;
        private long _telemetrySequence;
        private long _eventSequence;
        private readonly List<Dictionary<string, object>> _events = new List<Dictionary<string, object>>();
        private Dictionary<string, object> _telemetryCache;
        // Keep enough history for a no-visual client that polls at a normal
        // MCP cadence while a fast, frame-sliced build is running. The
        // response also reports when a cursor has fallen behind this window.
        private const int MaxTelemetryEvents = 2048;

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
            UpdateTelemetryCache(false);
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
            UpdateTelemetryCache(false);
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
                bool verboseLogging;
                if (bool.TryParse(node.GetValue("verboseLogging"), out verboseLogging)) _verboseLogging = verboseLogging;
                int maxRequests;
                if (int.TryParse(node.GetValue("maxRequestsPerFrame"), out maxRequests) && maxRequests > 0)
                {
                    _maxRequestsPerFrame = Math.Min(maxRequests, 32);
                }
                int telemetryIntervalMs;
                if (int.TryParse(node.GetValue("telemetryIntervalMs"), out telemetryIntervalMs) && telemetryIntervalMs > 0)
                {
                    _telemetryIntervalSeconds = Math.Max(0.025f, Math.Min(1f, telemetryIntervalMs / 1000f));
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
                    // Telemetry is deliberately served from the last cache on
                    // the listener thread. A long editor snapshot/analyze
                    // command must not make a high-rate visionless client
                    // wait behind the Unity main-thread request queue.
                    try
                    {
                        WriteResponse(context, Success(TelemetryFromCache(QueryArguments(context.Request))), 200);
                    }
                    catch (Exception exception)
                    {
                        WriteResponse(context, Failure("telemetry_unavailable", exception.Message, null), 503);
                    }
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
                case "game.list_saves": return ListSaves();
                case "game.load_save": return LoadSave(args);
                case "parts.list": return _craft.ListAvailableParts(args);
                case "editor.enter": return EnterEditor(args);
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
                case "editor.cancel_job": return _craft.CancelJob(args);
                case "flight.state": return _flight.Snapshot();
                case "flight.bodies": return _flight.Bodies(args);
                case "flight.maneuver_nodes": return _flight.ManeuverNodes();
                case "flight.add_maneuver_node": return _flight.AddManeuverNode(args);
                case "flight.clear_maneuver_nodes": return _flight.ClearManeuverNodes(args);
                case "flight.maneuver_burn_start": return _flight.StartManeuverBurn(args);
                case "flight.guidance_start": return _flight.StartGuidance(args);
                case "flight.moon_soft_landing_start": return _flight.StartMoonSoftLanding(args);
                case "flight.guidance_stop": return _flight.StopGuidance();
                case "flight.guidance_status": return _flight.GuidanceStatus();
                case "flight.guidance_update": return _flight.UpdateGuidance(args);
                case "flight.stage": return _flight.Stage();
                case "flight.set_controls": return _flight.SetControls(args);
                case "flight.set_sas": return _flight.SetSas(args);
                case "flight.set_rcs": return _flight.SetRcs(args);
                case "flight.warp": return _flight.Warp(args);
                case "flight.activate_part": return _flight.ActivatePart(args);
                case "flight.abort": return _flight.Abort();
                case "flight.clear_control": return _flight.ClearControl();
                case "flight.return_to_editor": return _flight.ReturnToEditor(args);
                case "flight.recover": return _flight.Recover();
                default: throw new KspMcpException("unknown_command", "unknown command: " + command, null);
            }
        }

        private Dictionary<string, object> EnterEditor(Dictionary<string, object> args)
        {
            string mode = JsonUtil.String(args, "editor_mode", "VAB").ToUpperInvariant();
            EditorFacility facility;
            if (mode == "VAB") facility = EditorFacility.VAB;
            else if (mode == "SPH") facility = EditorFacility.SPH;
            else throw new KspMcpException("invalid_editor_mode", "editor_mode must be VAB or SPH", mode);

            // EditorDriver can create an editor scene from the title screen,
            // but KSP's save-dependent editor UI and launch path require a
            // loaded Game object. Keep that bootstrap inside the bridge so a
            // no-visual client does not need to click through Load Game first.
            if (HighLogic.CurrentGame == null && JsonUtil.Get(args, "save_folder") != null)
            {
                LoadSave(args);
            }
            if (HighLogic.CurrentGame == null && HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                throw new KspMcpException("editor_enter_requires_save", "KSP is at the main menu without a loaded save; call game.load_save or pass save_folder to editor.enter", null);
            }

            if (HighLogic.LoadedScene == GameScenes.EDITOR)
            {
                return new Dictionary<string, object>
                {
                    { "scene_requested", "EDITOR" },
                    { "editor_mode", mode },
                    { "scene", SceneName() },
                    { "already_in_editor", true }
                };
            }

            try
            {
                // EditorDriver.StartEditor performs KSP's normal facility and
                // scene initialisation. Calling HighLogic.LoadScene directly
                // skips that setup and can leave EditorLogic unavailable.
                EditorDriver.StartEditor(facility);
                RecordEvent("editor.scene_requested", new Dictionary<string, object>
                {
                    { "scene", "EDITOR" },
                    { "editor_mode", mode },
                    { "entry", "mcp" }
                });
                return new Dictionary<string, object>
                {
                    { "scene_requested", "EDITOR" },
                    { "editor_mode", mode },
                    { "scene", SceneName() },
                    { "already_in_editor", false }
                };
            }
            catch (Exception exception)
            {
                throw new KspMcpException("editor_enter_failed", "KSP could not enter the editor: " + exception.Message, null);
            }
        }

        private Dictionary<string, object> ListSaves()
        {
            string root = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
            var saves = new List<object>();
            if (Directory.Exists(root))
            {
                foreach (string directory in Directory.GetDirectories(root))
                {
                    string persistent = Path.Combine(directory, "persistent.sfs");
                    if (!File.Exists(persistent)) continue;
                    DirectoryInfo info = new DirectoryInfo(directory);
                    saves.Add(new Dictionary<string, object>
                    {
                        { "save_folder", info.Name },
                        { "persistent_path", persistent },
                        { "last_write_utc", File.GetLastWriteTimeUtc(persistent).ToString("o") }
                    });
                }
            }
            return new Dictionary<string, object>
            {
                { "scene", SceneName() },
                { "current_save_folder", ReadStaticString(typeof(HighLogic), "SaveFolder") },
                { "saves", saves }
            };
        }

        private Dictionary<string, object> LoadSave(Dictionary<string, object> args)
        {
            if (HighLogic.LoadedScene == GameScenes.EDITOR || HighLogic.LoadedScene == GameScenes.FLIGHT)
            {
                throw new KspMcpException("game_load_unsafe", "load a save only from the main menu or space center", SceneName());
            }

            string saveFolder = JsonUtil.RequiredString(args, "save_folder");
            string root = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder);
            string persistent = Path.Combine(root, "persistent.sfs");
            if (!File.Exists(persistent))
            {
                throw new KspMcpException("save_not_found", "save folder does not contain persistent.sfs: " + saveFolder, persistent);
            }

            object loadedGame = null;
            MethodInfo usedMethod = null;
            string usedFileArgument = null;
            Exception lastError = null;
            var attempts = new List<string>();
            // KSP 1.12's public signature is LoadGame(filename, saveFolder,
            // ...).  filename is normally the logical save name "persistent",
            // not the absolute path returned by File.Exists.  Keep the path
            // and extension variants as compatibility fallbacks for forks.
            string[] filenameCandidates = { "persistent", persistent, "persistent.sfs" };
            foreach (string filename in filenameCandidates)
            {
                foreach (MethodInfo method in typeof(GamePersistence).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(method.Name, "LoadGame", StringComparison.Ordinal)) continue;
                    object[] invocation;
                    if (!TryBuildLoadGameArguments(method.GetParameters(), filename, saveFolder, out invocation)) continue;
                    try
                    {
                        loadedGame = method.Invoke(null, invocation);
                        attempts.Add(filename + ": return=" + (loadedGame == null ? "null" : loadedGame.GetType().FullName));
                        if (loadedGame != null)
                        {
                            SetStaticMember(typeof(HighLogic), "CurrentGame", loadedGame);
                            usedMethod = method;
                            usedFileArgument = filename;
                        }
                        if (HighLogic.CurrentGame != null) break;
                    }
                    catch (TargetInvocationException exception)
                    {
                        lastError = exception.InnerException ?? exception;
                        attempts.Add(filename + ": error=" + lastError.GetType().FullName + ":" + lastError.Message);
                    }
                    catch (Exception exception)
                    {
                        lastError = exception;
                        attempts.Add(filename + ": error=" + exception.GetType().FullName + ":" + exception.Message);
                    }
                }
                if (HighLogic.CurrentGame != null) break;
            }

            if (usedMethod == null)
            {
                string message = lastError == null ? "KSP returned no Game from GamePersistence.LoadGame" : lastError.Message;
                throw new KspMcpException("game_load_failed", "could not load save " + saveFolder + ": " + message, new Dictionary<string, object>
                {
                    { "attempts", attempts },
                    { "persistent_path", persistent }
                });
            }
            if (loadedGame != null) SetStaticMember(typeof(HighLogic), "CurrentGame", loadedGame);
            SetStaticMember(typeof(HighLogic), "SaveFolder", saveFolder);
            if (HighLogic.CurrentGame == null)
            {
                throw new KspMcpException("game_load_failed", "KSP returned no current game after loading save " + saveFolder, null);
            }

            HighLogic.LoadScene(GameScenes.SPACECENTER);
            RecordEvent("game.save_loaded", new Dictionary<string, object>
            {
                { "save_folder", saveFolder },
                { "scene", "SPACECENTER" },
                { "method", usedMethod.ToString() }
            });
            return new Dictionary<string, object>
            {
                { "loaded", true },
                { "save_folder", saveFolder },
                { "scene_requested", "SPACECENTER" },
                { "method", usedMethod.ToString() },
                { "file_argument", usedFileArgument }
            };
        }

        private static bool TryBuildLoadGameArguments(ParameterInfo[] parameters, string filename, string saveFolder, out object[] invocation)
        {
            invocation = null;
            var values = new object[parameters.Length];
            int stringIndex = 0;
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                Type type = parameter.ParameterType;
                if (type == typeof(string))
                {
                    string parameterName = (parameter.Name ?? "").ToLowerInvariant();
                    values[index] = stringIndex == 0 && parameterName.IndexOf("folder", StringComparison.Ordinal) < 0
                        ? filename
                        : saveFolder;
                    stringIndex++;
                }
                else if (type.IsEnum)
                {
                    try { values[index] = Enum.Parse(type, "OVERWRITE"); }
                    catch (Exception) { return false; }
                }
                else if (type == typeof(bool)) values[index] = false;
                else if (parameter.HasDefaultValue) values[index] = parameter.DefaultValue;
                else return false;
            }
            invocation = values;
            return true;
        }

        private static string ReadStaticString(Type type, string name)
        {
            object value = ReadStaticMember(type, name);
            return value == null ? null : value.ToString();
        }

        private static object ReadStaticMember(Type type, string name)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.CanRead) return property.GetValue(null, null);
                FieldInfo field = type.GetField(name, flags);
                if (field != null) return field.GetValue(null);
            }
            catch (Exception) { }
            return null;
        }

        private static void SetStaticMember(Type type, string name, object value)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, value, null);
                return;
            }
            FieldInfo field = type.GetField(name, flags);
            if (field != null && !field.IsInitOnly) field.SetValue(null, value);
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
                if (command == "batch" || command == "editor.launch" || command == "flight.abort" || command == "flight.recover" || command == "flight.maneuver_burn_start")
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
                { "bridge_version", "0.4.9" },
                { "scene", SceneName() },
                { "endpoint", "http://" + _host + ":" + _port },
                { "verbose_logging", _verboseLogging },
                { "telemetry_interval_ms", (int)(_telemetryIntervalSeconds * 1000f) },
                { "editor", _craft.Status() },
                { "flight", _flight.Snapshot() },
                { "capabilities", new Dictionary<string, object>
                    {
                        { "editor", new List<object> { "enter", "new", "snapshot", "apply", "add", "attach", "update", "remove", "stage", "action_group", "validate", "analyze", "save", "load", "launch", "job_status", "cancel_job" } },
                        { "flight", new List<object> { "state", "compact_telemetry", "bodies", "maneuver_nodes", "add_maneuver_node", "clear_maneuver_nodes", "maneuver_burn_start", "guidance_start", "moon_soft_landing_start", "guidance_stop", "guidance_status", "guidance_update", "stage", "controls", "sas", "rcs", "warp", "activate_part", "abort", "clear_control", "return_to_editor", "recover" } },
                        { "bridge", new List<object> { "telemetry", "batch", "game.list_saves", "game.load_save" } }
                    }
                }
            };
        }

        private Dictionary<string, object> Telemetry(Dictionary<string, object> args)
        {
            UpdateTelemetryCache(false);
            // Command-dispatched telemetry runs on Unity's main thread. Do
            // not let an optional HTTP long-poll wait stall that thread; the
            // GET endpoint below is the event-waiting path.
            var snapshotArgs = new Dictionary<string, object>(args ?? new Dictionary<string, object>());
            snapshotArgs.Remove("wait_ms");
            return TelemetryFromCache(snapshotArgs);
        }

        private Dictionary<string, object> TelemetryFromCache(Dictionary<string, object> args)
        {
            // This method is intentionally free of Unity/KSP calls. It is
            // used both by the main-thread dispatch path and directly by the
            // HTTP listener thread for low-latency polling.
            lock (_telemetryLock)
            {
                Dictionary<string, object> cache = _telemetryCache ?? new Dictionary<string, object>();
                long since = (long)Math.Max(0d, JsonUtil.Number(args, "since", 0d));
                int limit = Math.Max(1, Math.Min(256, JsonUtil.Integer(args, "limit", 64)));
                bool includeEvents = JsonUtil.Boolean(args, "include_events", true);
                int waitMs = Math.Max(0, Math.Min(1000, JsonUtil.Integer(args, "wait_ms", 0)));
                if (waitMs > 0 && since >= _eventSequence)
                {
                    // The listener thread may wait without touching Unity.
                    // RecordEvent pulses this condition as soon as a build or
                    // flight event is produced; a timeout simply returns the
                    // latest compact cache.
                    try { Monitor.Wait(_telemetryLock, waitMs); } catch (SynchronizationLockException) { }
                }
                var result = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> item in cache) result[item.Key] = item.Value;
                result["event_cursor"] = _eventSequence;
                long oldestEventCursor = _events.Count == 0 ? _eventSequence + 1 : (long)_events[0]["event_id"];
                long eventsLost = since < oldestEventCursor - 1 ? oldestEventCursor - 1 - since : 0;
                result["oldest_event_cursor"] = oldestEventCursor;
                result["events_lost"] = eventsLost;
                List<object> events = includeEvents ? EventsSinceLocked(since, limit) : new List<object>();
                result["events"] = events;
                long lastReturned = since;
                foreach (object raw in events)
                {
                    Dictionary<string, object> item = raw as Dictionary<string, object>;
                    if (item == null) continue;
                    lastReturned = Math.Max(lastReturned, (long)item["event_id"]);
                }
                // event_cursor is the producer cursor; next_since is the
                // consumer cursor. They differ when a response limit clips a
                // burst of build/staging events. A client that advances to
                // event_cursor in that case would silently skip the middle.
                result["events_returned"] = events.Count;
                result["events_truncated"] = includeEvents && eventsLost == 0 && lastReturned < _eventSequence;
                result["resync_required"] = eventsLost > 0;
                result["next_since"] = eventsLost > 0 ? _eventSequence : (includeEvents ? lastReturned : _eventSequence);
                return result;
            }
        }

        private List<object> EventsSinceLocked(long since, int limit)
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

        /*
         * Kept as a small wrapper for older internal callers. All callers
         * that need a consistent cursor should use TelemetryFromCache, which
         * owns the lock for the whole response construction.
         */
        private List<object> EventsSince(long since, int limit)
        {
            lock (_telemetryLock) return EventsSinceLocked(since, limit);
        }

        internal void RecordEvent(string type, object data)
        {
            lock (_telemetryLock)
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
                Monitor.PulseAll(_telemetryLock);
            }
        }

        private void UpdateTelemetryCache(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && _lastTelemetryAt >= 0f && now - _lastTelemetryAt < _telemetryIntervalSeconds) return;
            _lastTelemetryAt = now;
            _telemetrySequence++;
            Dictionary<string, object> nextCache = new Dictionary<string, object>
            {
                { "sequence", _telemetrySequence },
                { "bridge_version", "0.4.9" },
                { "captured_at", SafeUniversalTime() },
                { "scene", SceneName() },
                { "editor", _craft.CompactStatus() },
                { "flight", _flight.CompactSnapshot() }
            };
            lock (_telemetryLock) _telemetryCache = nextCache;
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
            int waitMs;
            if (int.TryParse(query["wait_ms"], out waitMs)) result["wait_ms"] = waitMs;
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
            if (Instance == null || !Instance._verboseLogging) return;
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

