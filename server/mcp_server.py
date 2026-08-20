"""A dependency-free MCP stdio server for the KSP bridge."""

from __future__ import annotations

import argparse
import json
import sys
import time
from typing import Any, Callable

from .bridge_client import BridgeClient, BridgeError
from .craft_model import CraftValidationError, normalise_part_spec, validate_craft_document
from .missions import MissionPlanError, build_space_station, plan_moon_landing
from .orbital import OrbitalPlanError, plan_circular_hohmann_transfer


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
            "wait_ms": {"type": "integer", "minimum": 0, "maximum": 1000},
        },
    ),
    _tool(
        "ksp_wait_for_event",
        "Wait for the next KSP telemetry event and return immediately when the event cursor advances; useful for low-latency no-visual construction and flight control.",
        {
            "since": {"type": "integer", "minimum": 0},
            "timeout": {"type": "number", "minimum": 0.05, "maximum": 30},
            "poll_interval": {"type": "number", "minimum": 0.02, "maximum": 1},
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
            "max_samples": {"type": "integer", "minimum": 1, "maximum": 240},
            "event_limit": {"type": "integer", "minimum": 1, "maximum": 256},
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
        "ksp_game_list_saves",
        "List real KSP save folders that can be loaded through the bridge; this is read-only and needs no visual UI.",
    ),
    _tool(
        "ksp_game_load_save",
        "Load one real KSP save through GamePersistence and request the Space Center scene, avoiding title-screen Computer Use.",
        {"save_folder": {"type": "string"}},
        ["save_folder"],
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
        "ksp_editor_enter",
        "Enter the real KSP VAB or SPH editor through the in-game bridge without visual UI automation.",
        {
            "editor_mode": {"type": "string", "enum": ["VAB", "SPH"]},
            "save_folder": {"type": "string", "description": "Optional save folder to load first when KSP is at the title screen."},
            "timeout": {"type": "number", "minimum": 1, "maximum": 60},
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
            "pace": {
                "type": "string",
                "enum": ["visible", "balanced", "fast"],
                "description": "Frame pacing for live construction: visible=one part per Unity frame, balanced=4, fast=16.",
            },
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
        "ksp_editor_cancel_job",
        "Stop an active frame-sliced editor build and keep the parts already generated for inspection or cleanup.",
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
        "Launch the current validated editor craft. Requires confirm=true. By default, MCP also clears occupied launch pads through KSP's stock recovery callback; set auto_clear_launchpad=false to leave the stock dialog open.",
        {
            "confirm": {"type": "boolean"},
            "auto_clear_launchpad": {"type": "boolean"},
        },
        ["confirm"],
    ),
    _tool("ksp_flight_state", "Read active-vessel telemetry, resources, engines, current stage, controls, and orbital values."),
    _tool(
        "ksp_flight_bodies",
        "Read celestial-body radii, gravitational parameters, atmospheres, spheres of influence, and patched-conic orbit data from the running KSP instance.",
        {"query": {"type": "string"}},
    ),
    _tool(
        "ksp_flight_transfer_plan",
        "Calculate a transparent circular-coplanar Hohmann estimate from the active vessel's body to a destination body. It is read-only and does not create or execute a burn.",
        {
            "destination_body": {"type": "string"},
            "origin_body": {"type": "string"},
            "parking_altitude_m": {"type": "number", "minimum": 0},
            "target_altitude_m": {"type": "number", "minimum": 0},
            "direction": {"type": "string", "enum": ["prograde"]},
        },
        ["destination_body"],
    ),
    _tool(
        "ksp_moon_landing_plan",
        "Read a live-data Kerbin-to-Mun (or current-body-to-moon) transfer estimate and the no-visual soft-landing handoff. This is read-only and does not create a node or burn.",
        {
            "target_body": {"type": "string", "description": "Defaults to Mun."},
            "parking_altitude_m": {"type": "number", "minimum": 0},
            "target_altitude_m": {"type": "number", "minimum": 0},
            "landing_latitude": {"type": "number", "minimum": -90, "maximum": 90},
            "landing_longitude": {"type": "number", "minimum": -180, "maximum": 180},
        },
    ),
    _tool(
        "ksp_flight_maneuver_nodes",
        "Read native KSP patched-conic maneuver nodes, including node-coordinate delta-v, burn vector, timing, and next-patch orbit.",
    ),
    _tool(
        "ksp_flight_add_maneuver_node",
        "Create one native KSP patched-conic maneuver node after explicit confirmation. Delta-v uses radial-plus, normal-plus, prograde coordinates in m/s.",
        {
            "ut": {"type": "number", "minimum": 0},
            "radial": {"type": "number"},
            "normal": {"type": "number"},
            "prograde": {"type": "number"},
            "confirm": {"type": "boolean"},
        },
        ["confirm"],
    ),
    _tool(
        "ksp_flight_clear_maneuver_nodes",
        "Remove all native KSP maneuver nodes after explicit confirmation.",
        {"confirm": {"type": "boolean"}},
        ["confirm"],
    ),
    _tool(
        "ksp_flight_guidance_start",
        "Start a game-side closed-loop guidance plan. It continuously refreshes controls in KSP frames; confirm=true is required. Profiles are ascent, orbit, landing, and node_burn.",
        {
            "profile": {"type": "string", "enum": ["ascent", "orbit", "landing", "node_burn"]},
            "target_apoapsis": {"type": "number", "minimum": 1000},
            "target_periapsis": {"type": "number", "minimum": 0},
            "target_altitude": {"type": "number", "minimum": 10},
            "auto_stage": {"type": "boolean"},
            "max_seconds": {"type": "number", "minimum": 5, "maximum": 3600},
            "confirm": {"type": "boolean"},
        },
        ["confirm"],
    ),
    _tool(
        "ksp_flight_moon_soft_landing_start",
        "Start the game-side Mun powered-descent controller after the vessel is at Mun. It checks the active body, then reuses the observable landing guidance loop; confirm=true is required.",
        {
            "target_body": {"type": "string", "description": "Defaults to Mun."},
            "target_latitude": {"type": "number", "minimum": -90, "maximum": 90},
            "target_longitude": {"type": "number", "minimum": -180, "maximum": 180},
            "deploy_gear": {"type": "boolean"},
            "gear_deploy_altitude": {"type": "number", "minimum": 100, "maximum": 10000},
            "auto_stage": {"type": "boolean"},
            "max_seconds": {"type": "number", "minimum": 5, "maximum": 3600},
            "confirm": {"type": "boolean"},
        },
        ["confirm"],
    ),
    _tool(
        "ksp_flight_maneuver_burn_start",
        "Start a confirmed, game-side finite-burn controller for one native KSP maneuver node. It aligns to the node burn vector, estimates burn duration from active thrust and mass, and exposes ignite/burn/complete phases through realtime telemetry.",
        {
            "node_index": {"type": "integer", "minimum": 0},
            "throttle": {"type": "number", "minimum": 0.1, "maximum": 1},
            "max_seconds": {"type": "number", "minimum": 5, "maximum": 3600},
            "confirm": {"type": "boolean"},
        },
        ["confirm"],
    ),
    _tool("ksp_flight_guidance_stop", "Stop the active closed-loop guidance plan and release MCP control."),
    _tool("ksp_flight_guidance_status", "Read the active guidance profile, phase, target, control output, and remaining time."),
    _tool(
        "ksp_flight_guidance_update",
        "Update targets and safety options on the active game-side guidance plan without releasing MCP control; useful for a no-visual real-time mission loop.",
        {
            "target_apoapsis": {"type": "number", "minimum": 1000},
            "target_periapsis": {"type": "number", "minimum": 0},
            "target_altitude": {"type": "number", "minimum": 10},
            "target_latitude": {"type": "number", "minimum": -90, "maximum": 90},
            "target_longitude": {"type": "number", "minimum": -180, "maximum": 180},
            "auto_stage": {"type": "boolean"},
            "deploy_gear": {"type": "boolean"},
            "gear_deploy_altitude": {"type": "number", "minimum": 100, "maximum": 10000},
            "extend_seconds": {"type": "number", "minimum": 0, "maximum": 3600},
        },
    ),
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
    _tool(
        "ksp_flight_return_to_editor",
        "Return from the current flight to the real VAB or SPH editor through KSP's native FlightDriver path, without visual UI automation.",
        {"editor_mode": {"type": "string", "enum": ["VAB", "SPH"]}},
    ),
    _tool("ksp_flight_recover", "Request recovery/revert of the current flight when KSP allows it."),
    _tool(
        "ksp_station_build",
        "Generate and build a connected stock orbital-station core in the real VAB. The result is deliberately modular so a person or another MCP call can add solar arrays, labs, and extra modules. Live construction emits the same per-part events as rocket building.",
        {
            "name": {"type": "string"},
            "template": {"type": "string", "enum": ["core"]},
            "live": {"type": "boolean"},
            "wait_for_completion": {"type": "boolean"},
            "pace": {"type": "string", "enum": ["visible", "balanced", "fast"]},
            "parts_per_frame": {"type": "integer", "minimum": 1, "maximum": 16},
            "require_connected": {"type": "boolean"},
        },
    ),
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
                wait_ms=int(args.get("wait_ms", 0)),
            )
        if name == "ksp_wait_for_event":
            since = max(0, int(args.get("since", 0)))
            timeout = max(0.05, min(30.0, float(args.get("timeout", 5.0))))
            poll_interval = max(0.02, min(1.0, float(args.get("poll_interval", 0.05))))
            limit = max(1, min(256, int(args.get("limit", 64))))
            include_events = bool(args.get("include_events", True))
            deadline = time.monotonic() + timeout
            latest: Any = None
            triggered = False
            last_wait_ms = 0
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                # The bridge waits on its event condition, so a normal event
                # arrival returns immediately while a quiet game costs one
                # bounded request instead of a tight polling loop.
                wait_ms = max(1, min(1000, int(remaining * 1000)))
                last_wait_ms = wait_ms
                latest = self.bridge.telemetry(
                    since=since,
                    limit=limit,
                    include_events=include_events,
                    wait_ms=wait_ms,
                )
                if isinstance(latest, dict):
                    cursor = latest.get("event_cursor")
                    lost = latest.get("events_lost", 0)
                    if isinstance(cursor, (int, float)) and int(cursor) > since:
                        triggered = True
                        break
                    if isinstance(lost, (int, float)) and int(lost) > 0:
                        triggered = True
                        break
                # Keep compatibility with bridges that do not implement the
                # optional wait_ms query parameter and return immediately.
                time.sleep(min(poll_interval, max(0.0, deadline - time.monotonic())))
            return {
                "since": since,
                "timeout_seconds": timeout,
                "poll_interval_seconds": poll_interval,
                "last_bridge_wait_ms": last_wait_ms,
                "triggered": triggered,
                "timed_out": not triggered,
                "state": latest,
            }
        if name == "ksp_watch":
            duration = max(0.1, min(60.0, float(args.get("duration", 5.0))))
            interval = max(0.05, min(2.0, float(args.get("interval", 0.2))))
            max_samples = max(1, min(240, int(args.get("max_samples", 120))))
            event_limit = max(1, min(256, int(args.get("event_limit", 256))))
            since = max(0, int(args.get("since", 0)))
            include_events = bool(args.get("include_events", True))
            samples: list[Any] = []
            deadline = time.monotonic() + duration
            effective_interval = duration if max_samples <= 1 else max(interval, duration / float(max_samples - 1))
            while True:
                # A frame-sliced build can emit several part events per Unity
                # frame.  Keep the watch path large enough to collect a full
                # polling window, otherwise advancing the cursor after a
                # truncated response silently loses the middle of the build.
                sample = self.bridge.telemetry(since=since, limit=event_limit, include_events=include_events)
                samples.append(sample)
                if isinstance(sample, dict) and isinstance(sample.get("next_since"), (int, float)):
                    since = int(sample["next_since"])
                elif isinstance(sample, dict) and isinstance(sample.get("event_cursor"), (int, float)):
                    since = int(sample["event_cursor"])
                if len(samples) >= max_samples:
                    break
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                time.sleep(min(effective_interval, remaining))
            return {
                "duration_seconds": duration,
                "interval_seconds": interval,
                "effective_interval_seconds": effective_interval,
                "max_samples": max_samples,
                "event_limit": event_limit,
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
            forbidden = {"editor.launch", "flight.abort", "flight.recover", "flight.maneuver_burn_start"}
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
        if name == "ksp_game_list_saves":
            return self.bridge.call("game.list_saves", {})
        if name == "ksp_game_load_save":
            save_folder = str(args.get("save_folder", "")).strip()
            if not save_folder:
                raise ValueError("ksp_game_load_save requires save_folder")
            requested = self.bridge.call("game.load_save", {"save_folder": save_folder})
            status = self.bridge.wait_for_scene("SPACECENTER", 30.0)
            if isinstance(status, dict):
                status["save_request"] = requested
            return status
        if name == "ksp_parts_list":
            return self.bridge.call("parts.list", args)
        if name == "ksp_editor_enter":
            requested = self.bridge.call("editor.enter", args)
            status = self.bridge.wait_for_scene("EDITOR", float(args.get("timeout", 30.0)))
            if isinstance(status, dict):
                status["scene_request"] = requested
            return status
        if name == "ksp_editor_new":
            return self.bridge.call("editor.new", args)
        if name == "ksp_editor_get_craft":
            return self.bridge.call("editor.snapshot", {})
        if name == "ksp_editor_apply_craft":
            craft = validate_craft_document(args["craft"], require_connected=bool(args.get("require_connected", False)))
            preflight_warnings = list(craft.pop("warnings", []))
            # Live, frame-sliced construction is the default for MCP callers:
            # it keeps Unity responsive and exposes progress to a visionless
            # client through ksp_editor_job_status and ksp_realtime_state.
            live = bool(args.get("live", not bool(args.get("wait_for_completion", False))))
            craft["live"] = live
            if "parts_per_frame" in args:
                craft["parts_per_frame"] = int(args["parts_per_frame"])
            elif live:
                # Balanced pacing keeps the editor responsive and still
                # exposes every part-added event.  Clients that need a slow
                # human-readable animation can request visible=1 explicitly;
                # fast=16 remains available for large station/rocket builds.
                pace = str(args.get("pace", "balanced")).strip().lower()
                pace_values = {"visible": 1, "balanced": 4, "fast": 16}
                if pace not in pace_values:
                    raise ValueError("pace must be visible, balanced, or fast")
                craft["parts_per_frame"] = pace_values[pace]
            result = self.bridge.call("editor.apply_craft", craft)
            if preflight_warnings and isinstance(result, dict):
                result["preflight_warnings"] = preflight_warnings
            return result
        if name == "ksp_editor_job_status":
            return self.bridge.call("editor.job_status", args)
        if name == "ksp_editor_cancel_job":
            return self.bridge.call("editor.cancel_job", args)
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
        if name == "ksp_flight_bodies":
            return self.bridge.call("flight.bodies", args)
        if name == "ksp_flight_transfer_plan":
            destination = args.get("destination_body")
            if not isinstance(destination, str) or not destination.strip():
                raise OrbitalPlanError("destination_body must be a non-empty body name")
            batch = self.bridge.call_batch(
                [
                    {"command": "flight.state", "args": {}},
                    {"command": "flight.bodies", "args": {"query": ""}},
                ]
            )
            if not isinstance(batch, dict) or not isinstance(batch.get("results"), list) or len(batch["results"]) < 2:
                raise OrbitalPlanError("the bridge returned no paired flight state/body data")
            state_item, bodies_item = batch["results"][0], batch["results"][1]
            if not isinstance(state_item, dict) or not state_item.get("ok"):
                raise OrbitalPlanError("flight.state failed while preparing the transfer plan")
            if not isinstance(bodies_item, dict) or not bodies_item.get("ok"):
                raise OrbitalPlanError("flight.bodies failed while preparing the transfer plan")
            return plan_circular_hohmann_transfer(
                bodies_payload=bodies_item.get("result") or {},
                flight_payload=state_item.get("result") or {},
                destination_body=destination,
                origin_body=args.get("origin_body"),
                parking_altitude_m=float(args.get("parking_altitude_m", 80_000.0)),
                target_altitude_m=float(args.get("target_altitude_m", 80_000.0)),
                direction=str(args.get("direction", "prograde")),
            )
        if name == "ksp_moon_landing_plan":
            batch = self.bridge.call_batch(
                [
                    {"command": "flight.state", "args": {}},
                    {"command": "flight.bodies", "args": {"query": ""}},
                ]
            )
            if not isinstance(batch, dict) or not isinstance(batch.get("results"), list) or len(batch["results"]) < 2:
                raise MissionPlanError("the bridge returned no paired flight state/body data")
            state_item, bodies_item = batch["results"][0], batch["results"][1]
            if not isinstance(state_item, dict) or not state_item.get("ok"):
                raise MissionPlanError("flight.state failed while preparing the moon landing plan")
            if not isinstance(bodies_item, dict) or not bodies_item.get("ok"):
                raise MissionPlanError("flight.bodies failed while preparing the moon landing plan")
            return plan_moon_landing(
                bodies_payload=bodies_item.get("result") or {},
                flight_payload=state_item.get("result") or {},
                target_body=str(args.get("target_body", "Mun")),
                parking_altitude_m=float(args.get("parking_altitude_m", 80_000.0)),
                target_altitude_m=float(args.get("target_altitude_m", 15_000.0)),
                landing_latitude=float(args.get("landing_latitude", 0.0)),
                landing_longitude=float(args.get("landing_longitude", 0.0)),
            )
        if name == "ksp_flight_maneuver_nodes":
            return self.bridge.call("flight.maneuver_nodes", {})
        if name == "ksp_flight_add_maneuver_node":
            if args.get("confirm") is not True:
                raise ValueError("ksp_flight_add_maneuver_node requires confirm=true")
            return self.bridge.call("flight.add_maneuver_node", args)
        if name == "ksp_flight_clear_maneuver_nodes":
            if args.get("confirm") is not True:
                raise ValueError("ksp_flight_clear_maneuver_nodes requires confirm=true")
            return self.bridge.call("flight.clear_maneuver_nodes", args)
        if name == "ksp_flight_maneuver_burn_start":
            if args.get("confirm") is not True:
                raise ValueError("ksp_flight_maneuver_burn_start requires confirm=true")
            return self.bridge.call("flight.maneuver_burn_start", args)
        if name == "ksp_flight_guidance_start":
            return self.bridge.call("flight.guidance_start", args)
        if name == "ksp_flight_moon_soft_landing_start":
            if args.get("confirm") is not True:
                raise ValueError("ksp_flight_moon_soft_landing_start requires confirm=true")
            return self.bridge.call("flight.moon_soft_landing_start", args)
        if name == "ksp_flight_guidance_stop":
            return self.bridge.call("flight.guidance_stop", {})
        if name == "ksp_flight_guidance_status":
            return self.bridge.call("flight.guidance_status", {})
        if name == "ksp_flight_guidance_update":
            return self.bridge.call("flight.guidance_update", args)
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
        if name == "ksp_flight_return_to_editor":
            return self.bridge.call("flight.return_to_editor", args)
        if name == "ksp_flight_recover":
            return self.bridge.call("flight.recover", {})
        if name == "ksp_station_build":
            craft = build_space_station(
                name=str(args.get("name", "MCP Orbital Station Core")),
                template=str(args.get("template", "core")),
            )
            craft_document = validate_craft_document(
                craft,
                require_connected=bool(args.get("require_connected", True)),
            )
            preflight_warnings = list(craft_document.pop("warnings", []))
            editor_result = self.bridge.call(
                "editor.new",
                {"name": craft_document["name"], "description": craft_document["description"], "editor_mode": "VAB"},
            )
            apply_args: dict[str, Any] = {
                "craft": craft_document,
                "require_connected": bool(args.get("require_connected", True)),
                "live": bool(args.get("live", True)),
            }
            for key in ("wait_for_completion", "pace", "parts_per_frame"):
                if key in args:
                    apply_args[key] = args[key]
            build_result = self.call_tool("ksp_editor_apply_craft", apply_args)
            return {
                "mission": "space_station_core",
                "template": str(args.get("template", "core")),
                "part_count": len(craft_document["parts"]),
                "preflight_warnings": preflight_warnings,
                "editor": editor_result,
                "build": build_result,
                "craft": craft_document,
            }
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
                "serverInfo": {"name": "kerbal-space-program", "version": "0.4.0"},
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
