import unittest

from server.mcp_server import KspMcpApplication, TOOLS, handle_message
from server.orbital import plan_circular_hohmann_transfer


class FakeBridge:
    def status(self):
        return {"scene": "EDITOR", "ok": True}

    def call(self, command, args):
        return {"command": command, "args": args}

    def wait_for_scene(self, scene, timeout):
        return {"scene": scene, "timeout": timeout}

    def telemetry(self, **kwargs):
        return {"sequence": 1, "event_cursor": 0, "scene": "EDITOR", "kwargs": kwargs}

    def call_batch(self, commands):
        return {"count": len(commands), "commands": commands}


class TransferBridge(FakeBridge):
    def call_batch(self, commands):
        if not commands:
            commands = [{"command": "flight.state"}, {"command": "flight.bodies"}]
        bodies = [
            {
                "name": "Sun",
                "radius_m": 261600000,
                "grav_parameter_m3_s2": 1.1723328e18,
                "reference_body": None,
            },
            {
                "name": "Kerbin",
                "radius_m": 600000,
                "grav_parameter_m3_s2": 3.5316e12,
                "reference_body": "Sun",
                "atmosphere": True,
                "max_atmosphere_altitude_m": 70000,
                "orbit": {"semi_major_axis_m": 13599840256},
            },
            {
                "name": "Duna",
                "radius_m": 320000,
                "grav_parameter_m3_s2": 3.0136321e11,
                "reference_body": "Sun",
                "atmosphere": True,
                "max_atmosphere_altitude_m": 50000,
                "orbit": {"semi_major_axis_m": 20726155264},
            },
        ]
        return {
            "count": 2,
            "results": [
                {"command": commands[0]["command"], "ok": True, "result": {"body": "Kerbin", "universal_time": 1234.0}},
                {"command": commands[1]["command"], "ok": True, "result": {"count": len(bodies), "bodies": bodies}},
            ],
        }


class EventBridge(FakeBridge):
    def __init__(self):
        self.calls = 0

    def telemetry(self, **kwargs):
        self.calls += 1
        return {
            "sequence": self.calls,
            "event_cursor": 1 if self.calls > 1 else 0,
            "oldest_event_cursor": 1,
            "events_lost": 0,
            "events": [] if self.calls == 1 else [{"event_id": 1, "type": "flight.liftoff"}],
            "kwargs": kwargs,
        }


class BurstBridge(FakeBridge):
    def __init__(self):
        self.since_values = []

    def telemetry(self, **kwargs):
        self.since_values.append(kwargs["since"])
        return {
            "sequence": len(self.since_values),
            "event_cursor": 3,
            "next_since": 1 if len(self.since_values) == 1 else 3,
            "events_truncated": len(self.since_values) == 1,
            "events_lost": 0,
            "events": [{"event_id": 1}],
        }


class McpProtocolTests(unittest.TestCase):
    def setUp(self):
        self.app = KspMcpApplication(FakeBridge())

    def test_initialize(self):
        response = handle_message(
            self.app,
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}},
        )
        self.assertEqual(response["result"]["serverInfo"]["name"], "kerbal-space-program")
        self.assertIn("tools", response["result"]["capabilities"])

    def test_tools_are_exposed(self):
        response = handle_message(
            self.app,
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
        )
        names = {item["name"] for item in response["result"]["tools"]}
        self.assertEqual(len(names), len(TOOLS))
        self.assertIn("ksp_editor_apply_craft", names)
        self.assertIn("ksp_flight_set_controls", names)
        self.assertIn("ksp_realtime_state", names)
        self.assertIn("ksp_wait_for_event", names)
        self.assertIn("ksp_editor_job_status", names)
        self.assertIn("ksp_editor_cancel_job", names)
        self.assertIn("ksp_flight_guidance_start", names)
        self.assertIn("ksp_flight_guidance_update", names)
        self.assertIn("ksp_flight_transfer_plan", names)
        self.assertIn("ksp_flight_maneuver_nodes", names)
        self.assertIn("ksp_flight_maneuver_burn_start", names)

    def test_realtime_state_routes_to_compact_bridge(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 5,
                "method": "tools/call",
                "params": {"name": "ksp_realtime_state", "arguments": {"since": 4, "limit": 3}},
            },
        )
        self.assertFalse(response["result"]["isError"])
        self.assertEqual(response["result"]["structuredContent"]["kwargs"]["since"], 4)

    def test_watch_is_bounded_for_no_visual_clients(self):
        result = self.app.call_tool(
            "ksp_watch",
            {"duration": 0.1, "interval": 0.05, "max_samples": 2, "event_limit": 256, "include_events": False},
        )
        self.assertLessEqual(result["sample_count"], 2)
        self.assertEqual(result["max_samples"], 2)
        self.assertEqual(result["event_limit"], 256)

    def test_wait_for_event_returns_when_cursor_advances(self):
        app = KspMcpApplication(EventBridge())
        result = app.call_tool("ksp_wait_for_event", {"since": 0, "timeout": 0.2, "limit": 8})
        self.assertTrue(result["triggered"])
        self.assertFalse(result["timed_out"])
        self.assertEqual(result["state"]["event_cursor"], 1)

    def test_watch_follows_consumer_cursor_when_response_is_truncated(self):
        bridge = BurstBridge()
        app = KspMcpApplication(bridge)
        result = app.call_tool(
            "ksp_watch",
            {"duration": 0.1, "interval": 0.05, "max_samples": 2, "event_limit": 1},
        )
        self.assertEqual(result["sample_count"], 2)
        self.assertEqual(bridge.since_values, [0, 1])

    def test_live_craft_defaults_to_frame_sliced_job(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 6,
                "method": "tools/call",
                "params": {
                    "name": "ksp_editor_apply_craft",
                    "arguments": {"craft": {"name": "live", "parts": [{"id": "pod", "part": "mk1pod.v2"}]}},
                },
            },
        )
        self.assertFalse(response["result"]["isError"])
        self.assertTrue(response["result"]["structuredContent"]["args"]["live"])
        self.assertEqual(response["result"]["structuredContent"]["args"]["parts_per_frame"], 12)

    def test_initialize_reports_current_server_version(self):
        response = handle_message(
            self.app,
            {"jsonrpc": "2.0", "id": 12, "method": "initialize", "params": {}},
        )
        self.assertEqual(response["result"]["serverInfo"]["version"], "0.3.0")

    def test_live_build_can_be_cancelled(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 11,
                "method": "tools/call",
                "params": {"name": "ksp_editor_cancel_job", "arguments": {"job_id": "build-1"}},
            },
        )
        self.assertFalse(response["result"]["isError"])
        self.assertEqual(response["result"]["structuredContent"]["command"], "editor.cancel_job")

    def test_guidance_update_routes_to_bridge(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 13,
                "method": "tools/call",
                "params": {
                    "name": "ksp_flight_guidance_update",
                    "arguments": {"target_apoapsis": 90000, "auto_stage": True},
                },
            },
        )
        self.assertFalse(response["result"]["isError"])
        self.assertEqual(response["result"]["structuredContent"]["command"], "flight.guidance_update")

    def test_batch_rejects_irreversible_commands(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 7,
                "method": "tools/call",
                "params": {"name": "ksp_batch", "arguments": {"commands": [{"command": "editor.launch"}]}},
            },
        )
        self.assertTrue(response["result"]["isError"])
        self.assertIn("dedicated confirmation tool", response["result"]["content"][0]["text"])

    def test_tool_call_routes_to_bridge(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {"name": "ksp_editor_set_stage", "arguments": {"id": "engine", "stage": 1}},
            },
        )
        self.assertFalse(response["result"]["isError"])
        self.assertEqual(response["result"]["structuredContent"]["command"], "editor.set_stage")

    def test_launch_needs_confirmation(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 4,
                "method": "tools/call",
                "params": {"name": "ksp_editor_launch", "arguments": {"confirm": False}},
            },
        )
        self.assertTrue(response["result"]["isError"])
        self.assertIn("confirm=true", response["result"]["content"][0]["text"])

    def test_transfer_math_returns_explicit_estimate(self):
        bodies = TransferBridge().call_batch([])["results"][1]["result"]
        state = {"body": "Kerbin", "universal_time": 1234.0}
        result = plan_circular_hohmann_transfer(
            bodies_payload=bodies,
            flight_payload=state,
            destination_body="Duna",
        )
        self.assertEqual(result["origin_body"], "Kerbin")
        self.assertEqual(result["destination_body"], "Duna")
        self.assertGreater(result["transfer_time_days"], 50)
        self.assertGreater(result["total_estimated_delta_v_mps"], 0)
        self.assertIn("no plane change", " ".join(result["assumptions"]))

    def test_transfer_plan_uses_one_batched_bridge_call(self):
        app = KspMcpApplication(TransferBridge())
        response = handle_message(
            app,
            {
                "jsonrpc": "2.0",
                "id": 8,
                "method": "tools/call",
                "params": {
                    "name": "ksp_flight_transfer_plan",
                    "arguments": {"destination_body": "Duna"},
                },
            },
        )
        self.assertFalse(response["result"]["isError"])
        self.assertEqual(response["result"]["structuredContent"]["reference_body"], "Sun")

    def test_maneuver_node_write_needs_confirmation(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 9,
                "method": "tools/call",
                "params": {
                    "name": "ksp_flight_add_maneuver_node",
                    "arguments": {"prograde": 100.0, "confirm": False},
                },
            },
        )
        self.assertTrue(response["result"]["isError"])
        self.assertIn("confirm=true", response["result"]["content"][0]["text"])

    def test_maneuver_burn_needs_confirmation(self):
        response = handle_message(
            self.app,
            {
                "jsonrpc": "2.0",
                "id": 10,
                "method": "tools/call",
                "params": {
                    "name": "ksp_flight_maneuver_burn_start",
                    "arguments": {"node_index": 0, "confirm": False},
                },
            },
        )
        self.assertTrue(response["result"]["isError"])
        self.assertIn("confirm=true", response["result"]["content"][0]["text"])


if __name__ == "__main__":
    unittest.main()
