"""A dependency-free MCP stdio server for the KSP bridge."""

from __future__ import annotations

import argparse
import json
import sys
import time
from typing import Any, Callable

from .bridge_client import BridgeClient, BridgeError
from .craft_model import CraftValidationError, normalise_part_spec, validate_craft_document


def _object_schema(properties: dict[str, Any] | None = None, required: list[str] | None = None) -> dict[str, Any]:
    return {
        "type": "object",
        "properties": properties or {},
        "required": required or [],
        "additionalProperties": True,
    }


_VECTOR3 = {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3}
_VECTOR4 = {"type": "array", "items": {"type": "number"}, "minItems": 4, "maxItems": 4}


CRAFT_SCHEMA = _object_schema(
    {
        "name": {"type": "string"},
        "description": {"type": "string"},
        "editor_mode": {"type": "string", "enum": ["VAB", "SPH"]},
        "parts": {
            "type": "array",
            "items": _object_schema(
                {
                    "id": {"type": "string"},
                    "part": {"type": "string"},
                    "parent_id": {"type": ["string", "null"]},
                    "parent_attach_node": {"type": ["string", "null"]},
                    "attach_node": {"type": ["string", "null"]},
                    "position": _VECTOR3,
                    "rotation": _VECTOR4,
                    "stage": {"type": "integer", "minimum": 0},
                    "action_groups": {"type": "object"},
                    "variant": {"type": "string"},
                    "custom_data": {"type": "object"},
                    "snap_to_node": {"type": "boolean"},
                },
                required=["part"],
            ),
        },
    }
)


def _tool(name: str, description: str, properties: dict[str, Any] | None = None, required: list[str] | None = None) -> dict[str, Any]:
    return {
        "name": name,
        "description": description,
        "inputSchema": _object_schema(properties, required),
    }


TOOLS: list[dict[str, Any]] = [
    _tool("ksp_status", "Read the current KSP scene, editor craft summary, flight telemetry, and bridge capabilities."),
    _tool(
        "ksp_realtime_state",
        "Read compact cached no-visual telemetry and incremental KSP events. Prefer this over ksp_status while building or flying.",
        {
            "since": {"type": "integer", "minimum": 0},
            "limit": {"type": "integer", "minimum": 1, "maximum": 256},
            "include_events": {"type": "boolean"},
        },
    ),
    _tool(
        "ksp_watch",
        "Sample compact telemetry for a bounded interval so a model without vision can observe construction, staging, ascent, orbit, or landing in real time.",
        {
            "duration": {"type": "number", "minimum": 0.1, "maximum": 60},
            "interval": {"type": "number", "minimum": 0.05, "maximum": 2},
            "since": {"type": "integer", "minimum": 0},
            "include_events": {"type": "boolean"},
        },
    ),
    _tool(
        "ksp_batch",
        "Send several safe KSP commands in one bridge round trip to reduce latency. Irreversible launch, abort, and recovery commands must use their dedicated tools.",
        {
            "commands": {
                "type": "array",
                "minItems": 1,
                "maxItems": 32,
                "items": _object_schema({"command": {"type": "string"}, "args": {"type": "object"}}, ["command"]),
            }
        },
        ["commands"],
    ),
    _tool(
        "ksp_wait_for_scene",
        "Wait until KSP reaches a requested scene such as EDITOR or FLIGHT.",
        {"scene": {"type": "string"}, "timeout": {"type": "number", "minimum": 0.1}},
        ["scene"],
    ),
    _tool(
        "ksp_parts_list",
        "List the actual parts loaded by the running KSP instance, including attachment nodes and modules.",
        {
            "query": {"type": "string"},
            "include_modules": {"type": "boolean"},
            "limit": {"type": "integer", "minimum": 1, "maximum": 1000},
        },
    ),
    _tool(
        "ksp_editor_new",
        "Start a new empty VAB or SPH craft and remove the current editor craft.",
        {"name": {"type": "string"}, "description": {"type": "string"}, "editor_mode": {"type": "string", "enum": ["VAB", "SPH"]}},
    ),
    _tool("ksp_editor_get_craft", "Return the full current craft tree, part transforms, nodes, modules, resources, and staging."),
    _tool(
        "ksp_editor_apply_craft",
        "Replace the editor with a complete craft document built from zero. This is the main bulk-building tool.",
        {
            "craft": CRAFT_SCHEMA,
            "require_connected": {"type": "boolean"},
            "live": {"type": "boolean"},
            "wait_for_completion": {"type": "boolean"},
            "parts_per_frame": {"type": "integer", "minimum": 1, "maximum": 16},
        },
        ["craft"],
    ),
    _tool(
        "ksp_editor_job_status",
        "Read the progress of an asynchronous editor build or load job without requesting the full craft tree.",
        {"job_id": {"type": "string"}},
        ["job_id"],
    ),
    _tool(
        "ksp_editor_add_part",
        "Add one real KSP part to the current editor craft, optionally attached to an existing part.",
        {
            "id": {"type": "string"},
            "part": {"type": "string"},
            "parent_id": {"type": ["string", "null"]},
            "parent_attach_node": {"type": ["string", "null"]},
            "attach_node": {"type": ["string", "null"]},
            "position": _VECTOR3,
            "rotation": _VECTOR4,
            "stage": {"type": "integer", "minimum": 0},
            "action_groups": {"type": "object"},
            "variant": {"type": "string"},
            "snap_to_node": {"type": "boolean"},
        },
        ["id", "part"],
    ),
    _tool(
        "ksp_editor_attach_part",
        "Reconnect an existing editor part to a parent using explicit KSP attachment nodes.",
        {
            "id": {"type": "string"},
            "parent_id": {"type": "string"},
            "parent_attach_node": {"type": "string"},
            "attach_node": {"type": "string"},
            "snap_to_node": {"type": "boolean"},
        },
        ["id", "parent_id", "parent_attach_node", "attach_node"],
    ),
    _tool(
        "ksp_editor_update_part",
        "Move, rotate, reparent, change stage, or update action groups on an editor part.",
        {
            "id": {"type": "string"},
            "position": _VECTOR3,
            "rotation": _VECTOR4,
            "parent_id": {"type": ["string", "null"]},
            "parent_attach_node": {"type": "string"},
            "attach_node": {"type": "string"},
            "snap_to_node": {"type": "boolean"},
            "stage": {"type": "integer", "minimum": 0},
            "action_groups": {"type": "object"},
        },
        ["id"],
    ),
    _tool(
        "ksp_editor_remove_part",
        "Remove an editor part and, by default, its complete child subtree.",
        {"id": {"type": "string"}, "include_children": {"type": "boolean"}},
        ["id"],
    ),
    _tool(
        "ksp_editor_set_stage",
        "Set the inverse/original stage number for an editor part.",
        {"id": {"type": "string"}, "stage": {"type": "integer", "minimum": 0}},
        ["id", "stage"],
    ),
    _tool(
        "ksp_editor_set_action_group",
        "Assign a part action to a stock KSP action group such as SAS, RCS, Abort, Custom01, or None.",
        {"id": {"type": "string"}, "action": {"type": "string"}, "group": {"type": "string"}},
        ["id", "action", "group"],
    ),
    _tool("ksp_editor_validate", "Validate the current editor craft and return errors, warnings, cost, connectivity, and part counts."),
    _tool(
        "ksp_editor_analyze",
        "Analyze real KSP part modules for approximate mass, thrust-to-weight, staging delta-v, center-of-mass/thrust geometry, and launch-risk warnings.",
        {"include_parts": {"type": "boolean"}},
    ),
    _tool(
        "ksp_editor_save",
        "Save the current editor craft as a real KSP .craft file.",
        {"name": {"type": "string"}, "path": {"type": "string"}, "overwrite": {"type": "boolean"}},
    ),
    _tool(
        "ksp_editor_load",
        "Load a real KSP .craft file into the active VAB/SPH editor.",
        {"path": {"type": "string"}, "name": {"type": "string"}, "editor_mode": {"type": "string", "enum": ["VAB", "SPH"]}},
    ),
    _tool("ksp_editor_clear", "Clear every part from the current editor craft."),
    _tool(
        "ksp_editor_launch",
        "Launch the current validated editor craft. Requires confirm=true to make the irreversible transition.",
        {"confirm": {"type": "boolean"}},
        ["confirm"],
    ),
    _tool("ksp_flight_state", "Read active-vessel telemetry, resources, engines, current stage, controls, and orbital values."),
    _tool(
        "ksp_flight_guidance_start",
        "Start a game-side closed-loop guidance plan. It continuously refreshes controls in KSP frames; confirm=true is required. Profiles are ascent, orbit, and landing.",
        {
            "profile": {"type": "string", "enum": ["ascent", "orbit", "landing"]},
            "target_apoapsis": {"type": "number", "minimum": 1000},
            "target_periapsis": {"type": "number", "minimum": 0},
            "target_altitude": {"type": "number", "minimum": 10},
            "auto_stage": {"type": "boolean"},
            "max_seconds": {"type": "number", "minimum": 5, "maximum": 3600},
            "confirm": {"type": "boolean"},
        },
        ["confirm"],
    ),
    _tool("ksp_flight_guidance_stop", "Stop the active closed-loop guidance plan and release MCP control."),
    _tool("ksp_flight_guidance_status", "Read the active guidance profile, phase, target, control output, and remaining time."),
    _tool("ksp_flight_stage", "Activate the next KSP staging step."),
    _tool(
        "ksp_flight_set_controls",
        "Set a short-lived fly-by-wire control lease for throttle, pitch, yaw, roll, translation, wheels, gear, brakes, or lights.",
        {
            "throttle": {"type": "number", "minimum": 0, "maximum": 1},
            "pitch": {"type": "number", "minimum": -1, "maximum": 1},
            "yaw": {"type": "number", "minimum": -1, "maximum": 1},
            "roll": {"type": "number", "minimum": -1, "maximum": 1},
            "translate_x": {"type": "number", "minimum": -1, "maximum": 1},
            "translate_y": {"type": "number", "minimum": -1, "maximum": 1},
            "translate_z": {"type": "number", "minimum": -1, "maximum": 1},
            "wheel_steer": {"type": "number", "minimum": -1, "maximum": 1},
            "wheel_throttle": {"type": "number", "minimum": -1, "maximum": 1},
            "gear": {"type": "boolean"},
            "brakes": {"type": "boolean"},
            "lights": {"type": "boolean"},
            "lease_seconds": {"type": "number", "minimum": 0.1, "maximum": 30},
        },
    ),
    _tool(
        "ksp_flight_set_sas",
        "Enable or disable stock SAS using the active vessel action group.",
        {"enabled": {"type": "boolean"}},
        ["enabled"],
    ),
    _tool(
        "ksp_flight_set_rcs",
        "Enable or disable stock RCS using the active vessel action group.",
        {"enabled": {"type": "boolean"}},
        ["enabled"],
    ),
    _tool(
        "ksp_flight_warp",
        "Set the stock time-warp rate index. Use zero for real time.",
        {"rate_index": {"type": "integer", "minimum": 0, "maximum": 20}},
        ["rate_index"],
    ),
    _tool(
        "ksp_flight_activate_part",
        "Trigger a part event/action by MCP part id or vessel flight id.",
        {"part_id": {"type": "string"}, "event": {"type": "string"}, "flight_id": {"type": "integer"}},
    ),
    _tool("ksp_flight_abort", "Fire the stock Abort action group."),
    _tool("ksp_flight_clear_control", "Release the MCP fly-by-wire control lease and return control to KSP/player input."),
    _tool("ksp_flight_recover", "Request recovery/revert of the current flight when KSP allows it."),
]


class KspMcpApplication:
    def __init__(self, bridge: BridgeClient | None = None) -> None:
        self.bridge = bridge or BridgeClient()

    def call_tool(self, name: str, arguments: dict[str, Any] | None) -> Any:
        args = arguments or {}

        if name == "ksp_status":
            return self.bridge.status()
        if name == "ksp_realtime_state":
            return self.bridge.telemetry(
                since=int(args.get("since", 0)),
                limit=int(args.get("limit", 64)),
                include_events=bool(args.get("include_events", True)),
            )
        if name == "ksp_watch":
            duration = max(0.1, min(60.0, float(args.get("duration", 5.0))))
            interval = max(0.05, min(2.0, float(args.get("interval", 0.2))))
            since = max(0, int(args.get("since", 0)))
            include_events = bool(args.get("include_events", True))
            samples: list[Any] = []
            deadline = time.monotonic() + duration
            while True:
                sample = self.bridge.telemetry(since=since, limit=64, include_events=include_events)
                samples.append(sample)
                if isinstance(sample, dict) and isinstance(sample.get("event_cursor"), (int, float)):
                    since = int(sample["event_cursor"])
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                time.sleep(min(interval, remaining))
            return {
                "duration_seconds": duration,
                "interval_seconds": interval,
                "sample_count": len(samples),
                "samples": samples,
            }
        if name == "ksp_batch":
            commands = args.get("commands")
            if not isinstance(commands, list) or not commands:
                raise ValueError("ksp_batch requires a non-empty commands array")
            if len(commands) > 32:
                raise ValueError("ksp_batch accepts at most 32 commands")
            normalised: list[dict[str, Any]] = []
            forbidden = {"editor.launch", "flight.abort", "flight.recover"}
            for index, item in enumerate(commands):
                if not isinstance(item, dict) or not isinstance(item.get("command"), str):
                    raise ValueError(f"commands[{index}] requires a command string")
                command = item["command"]
                if command in forbidden:
                    raise ValueError(f"{command} must use its dedicated confirmation tool")
                normalised.append({"command": command, "args": item.get("args") or {}})
            return self.bridge.call_batch(normalised)
        if name == "ksp_wait_for_scene":
            return self.bridge.wait_for_scene(str(args["scene"]), float(args.get("timeout", 30.0)))
        if name == "ksp_parts_list":
            return self.bridge.call("parts.list", args)
        if name == "ksp_editor_new":
            return self.bridge.call("editor.new", args)
        if name == "ksp_editor_get_craft":
            return self.bridge.call("editor.snapshot", {})
        if name == "ksp_editor_apply_craft":
            craft = validate_craft_document(args["craft"], require_connected=bool(args.get("require_connected", False)))
            craft.pop("warnings", None)
            # Live, frame-sliced construction is the default for MCP callers:
            # it keeps Unity responsive and exposes progress to a visionless
            # client through ksp_editor_job_status and ksp_realtime_state.
            live = bool(args.get("live", not bool(args.get("wait_for_completion", False))))
            craft["live"] = live
            if "parts_per_frame" in args:
                craft["parts_per_frame"] = int(args["parts_per_frame"])
            return self.bridge.call("editor.apply_craft", craft)
        if name == "ksp_editor_job_status":
            return self.bridge.call("editor.job_status", args)
        if name == "ksp_editor_add_part":
            return self.bridge.call("editor.add_part", normalise_part_spec(args))
        if name == "ksp_editor_attach_part":
            return self.bridge.call("editor.attach_part", args)
        if name == "ksp_editor_update_part":
            return self.bridge.call("editor.update_part", args)
        if name == "ksp_editor_remove_part":
            return self.bridge.call("editor.remove_part", args)
        if name == "ksp_editor_set_stage":
            return self.bridge.call("editor.set_stage", args)
        if name == "ksp_editor_set_action_group":
            return self.bridge.call("editor.set_action_group", args)
        if name == "ksp_editor_validate":
            return self.bridge.call("editor.validate", {})
        if name == "ksp_editor_analyze":
            return self.bridge.call("editor.analyze", args)
        if name == "ksp_editor_save":
            return self.bridge.call("editor.save", args)
        if name == "ksp_editor_load":
            return self.bridge.call("editor.load", args)
        if name == "ksp_editor_clear":
            return self.bridge.call("editor.clear", {})
        if name == "ksp_editor_launch":
            if args.get("confirm") is not True:
                raise CraftValidationError("ksp_editor_launch requires confirm=true")
            return self.bridge.call("editor.launch", args)
        if name == "ksp_flight_state":
            return self.bridge.call("flight.state", {})
        if name == "ksp_flight_guidance_start":
            return self.bridge.call("flight.guidance_start", args)
        if name == "ksp_flight_guidance_stop":
            return self.bridge.call("flight.guidance_stop", {})
        if name == "ksp_flight_guidance_status":
            return self.bridge.call("flight.guidance_status", {})
        if name == "ksp_flight_stage":
            return self.bridge.call("flight.stage", {})
        if name == "ksp_flight_set_controls":
            return self.bridge.call("flight.set_controls", args)
        if name == "ksp_flight_set_sas":
            return self.bridge.call("flight.set_sas", args)
        if name == "ksp_flight_set_rcs":
            return self.bridge.call("flight.set_rcs", args)
        if name == "ksp_flight_warp":
            return self.bridge.call("flight.warp", args)
        if name == "ksp_flight_activate_part":
            return self.bridge.call("flight.activate_part", args)
        if name == "ksp_flight_abort":
            return self.bridge.call("flight.abort", {})
        if name == "ksp_flight_clear_control":
            return self.bridge.call("flight.clear_control", {})
        if name == "ksp_flight_recover":
            return self.bridge.call("flight.recover", {})
        raise KeyError(f"unknown tool: {name}")


def _json_line(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _send(message: dict[str, Any]) -> None:
    sys.stdout.write(_json_line(message) + "\n")
    sys.stdout.flush()


def _success(request_id: Any, result: Any) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def _error(request_id: Any, code: int, message: str, data: Any = None) -> dict[str, Any]:
    error: dict[str, Any] = {"code": code, "message": message}
    if data is not None:
        error["data"] = data
    return {"jsonrpc": "2.0", "id": request_id, "error": error}


def handle_message(app: KspMcpApplication, message: dict[str, Any]) -> dict[str, Any] | None:
    method = message.get("method")
    request_id = message.get("id")
    params = message.get("params") or {}

    if method in ("notifications/initialized", "notifications/cancelled"):
        return None
    if method == "ping":
        return _success(request_id, {})
    if method == "initialize":
        return _success(
            request_id,
            {
                "protocolVersion": str(params.get("protocolVersion", "2024-11-05")),
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "kerbal-space-program", "version": "0.2.0"},
                "instructions": (
                    "Use ksp_realtime_state for compact no-visual state. Build in VAB/SPH with "
                    "ksp_editor_new and live ksp_editor_apply_craft, then poll "
                    "ksp_editor_job_status until complete, validate and save before launching."
                ),
            },
        )
    if method == "tools/list":
        return _success(request_id, {"tools": TOOLS})
    if method == "tools/call":
        name = params.get("name")
        if not isinstance(name, str):
            return _error(request_id, -32602, "tools/call requires params.name")
        try:
            result = app.call_tool(name, params.get("arguments"))
            content = [{"type": "text", "text": json.dumps(result, ensure_ascii=False, indent=2)}]
            return _success(request_id, {"content": content, "structuredContent": result, "isError": False})
        except (BridgeError, CraftValidationError, KeyError, TypeError, ValueError) as exc:
            details: dict[str, Any] = {"type": type(exc).__name__}
            if isinstance(exc, BridgeError):
                details.update({"code": exc.code, "details": exc.details})
            text = str(exc)
            return _success(
                request_id,
                {
                    "content": [{"type": "text", "text": text}],
                    "isError": True,
                    "structuredContent": {"error": text, "details": details},
                },
            )
    return _error(request_id, -32601, f"method not found: {method}")


def run_stdio(app: KspMcpApplication | None = None) -> None:
    application = app or KspMcpApplication()
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            message = json.loads(line)
            if not isinstance(message, dict):
                raise ValueError("JSON-RPC message must be an object")
            response = handle_message(application, message)
            if response is not None:
                _send(response)
        except json.JSONDecodeError as exc:
            _send(_error(None, -32700, "parse error", str(exc)))
        except Exception as exc:  # Keep the stdio protocol alive for one bad request.
            _send(_error(None, -32603, "internal error", str(exc)))


def _self_test() -> int:
    class FakeBridge:
        def status(self) -> dict[str, Any]:
            return {"scene": "EDITOR", "bridge": "fake"}

        def call(self, command: str, args: dict[str, Any]) -> dict[str, Any]:
            return {"command": command, "args": args}

        def wait_for_scene(self, scene: str, timeout: float) -> dict[str, Any]:
            return {"scene": scene, "timeout": timeout}

    app = KspMcpApplication(FakeBridge())  # type: ignore[arg-type]
    assert handle_message(app, {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}})["result"]
    tools = handle_message(app, {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
    assert len(tools["result"]["tools"]) >= 20
    status = handle_message(app, {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "ksp_status", "arguments": {}}})
    assert status["result"]["structuredContent"]["scene"] == "EDITOR"
    print(f"self-test ok: {len(TOOLS)} tools")
    return 0


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true", help="run local protocol checks and exit")
    args = parser.parse_args(argv)
    if args.self_test:
        raise SystemExit(_self_test())
    run_stdio()
