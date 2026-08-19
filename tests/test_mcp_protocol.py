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
        self.assertIn("ksp_editor_job_status", names)
        self.assertIn("ksp_flight_guidance_start", names)
        self.assertIn("ksp_flight_transfer_plan", names)
        self.assertIn("ksp_flight_maneuver_nodes", names)

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


if __name__ == "__main__":
    unittest.main()
