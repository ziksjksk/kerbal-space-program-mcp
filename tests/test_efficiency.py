import json
import queue
import threading
import unittest
from unittest.mock import patch

from server.bridge_client import BridgeClient
from server.craft_model import CraftValidationError, validate_craft_document
from server.mcp_server import KspMcpApplication, _error, handle_message
from server.request_context import pause
from server.stdio_runtime import serve


def request(number, name, **arguments):
    return {"jsonrpc": "2.0", "id": number, "method": "tools/call",
            "params": {"name": name, "arguments": arguments}}


class Inputs:
    def __init__(self):
        self.lines = queue.Queue()

    def put(self, message):
        self.lines.put(json.dumps(message) + "\n")

    def __iter__(self):
        while True:
            line = self.lines.get()
            if line is None:
                return
            yield line


class BlockingBridge:
    def __init__(self):
        self.observing = threading.Event()
        self.command_started = threading.Event()
        self.release_command = threading.Event()
        self.commands = []

    def telemetry(self, **kwargs):
        self.observing.set()
        pause(0.1)  # Cooperative simulated long-poll.
        return {"event_cursor": 0, "next_since": 0, "events": []}

    def call(self, name, args):
        self.commands.append(name)
        if name == "editor.new":
            self.command_started.set()
            if not self.release_command.wait(3):
                raise RuntimeError("test did not release command")
        return {"command": name}


class SchedulingTests(unittest.TestCase):
    def setUp(self):
        self.source = Inputs()
        self.output = queue.Queue()
        self.bridge = BlockingBridge()
        self.thread = threading.Thread(target=serve, args=(
            KspMcpApplication(self.bridge), handle_message, self.output.put, _error, self.source))
        self.thread.start()

    def tearDown(self):
        self.bridge.release_command.set()
        self.source.lines.put(None)
        self.thread.join(4)
        self.assertFalse(self.thread.is_alive(), "stdio did not shut down")

    def test_observation_does_not_block_ping_or_controls(self):
        self.source.put(request(1, "ksp_wait_for_event", timeout=30))
        self.assertTrue(self.bridge.observing.wait(2))
        self.source.put({"jsonrpc": "2.0", "id": 2, "method": "ping"})
        self.assertEqual(self.output.get(timeout=2)["id"], 2)
        self.source.put(request(3, "ksp_flight_guidance_stop"))
        result = self.output.get(timeout=2)
        self.assertEqual(result["id"], 3)
        self.assertEqual(result["result"]["structuredContent"]["command"], "flight.guidance_stop")

    def test_cancelled_queued_action_is_not_dispatched(self):
        self.source.put(request(1, "ksp_editor_new"))
        self.assertTrue(self.bridge.command_started.wait(2))
        self.source.put(request(2, "ksp_flight_stage"))
        self.source.put({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 2}})
        self.source.put({"jsonrpc": "2.0", "id": 3, "method": "ping"})
        self.assertEqual(self.output.get(timeout=2)["id"], 3)
        self.bridge.release_command.set()
        self.assertEqual(self.output.get(timeout=2)["id"], 1)
        self.source.put(request(4, "ksp_flight_guidance_stop"))
        self.assertEqual(self.output.get(timeout=2)["id"], 4)
        self.assertEqual(self.bridge.commands, ["editor.new", "flight.guidance_stop"])

    def test_mutations_keep_fifo_order(self):
        self.source.put(request(1, "ksp_editor_new"))
        self.assertTrue(self.bridge.command_started.wait(2))
        self.source.put(request(2, "ksp_flight_stage"))
        self.source.put(request(3, "ksp_flight_guidance_stop"))
        self.bridge.release_command.set()
        self.assertEqual([self.output.get(timeout=2)["id"] for _ in range(3)], [1, 2, 3])

    def test_observation_overload_does_not_fill_control_queue(self):
        for number in range(1, 6):
            self.source.put(request(number, "ksp_wait_for_event", timeout=30))
        result = self.output.get(timeout=2)
        self.assertEqual(result["id"], 5)
        self.assertEqual(result["error"]["message"], "server busy")
        self.source.put(request(6, "ksp_flight_guidance_stop"))
        self.assertEqual(self.output.get(timeout=2)["id"], 6)

    def test_cancelled_observation_frees_capacity(self):
        self.source.put(request(1, "ksp_wait_for_event", timeout=30))
        self.assertTrue(self.bridge.observing.wait(2))
        self.source.put({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 1}})
        self.source.put(request(2, "ksp_watch", duration=0.1, max_samples=1))
        self.assertEqual(self.output.get(timeout=2)["id"], 2)

    def test_bad_message_does_not_kill_stdio(self):
        self.source.lines.put("{broken\n")
        self.assertEqual(self.output.get(timeout=2)["error"]["code"], -32700)
        self.source.put([])
        self.assertEqual(self.output.get(timeout=2)["error"]["code"], -32600)
        self.source.put({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": []})
        self.assertEqual(self.output.get(timeout=2)["error"]["code"], -32602)
        self.source.put({"jsonrpc": "2.0", "id": 2, "method": "ping"})
        self.assertEqual(self.output.get(timeout=2)["id"], 2)


class EfficiencyTests(unittest.TestCase):
    def test_deep_unordered_tree_and_detached_cycle(self):
        parts = [{"id": str(i), "part": "fuelTankSmall", "parent_id": str(i-1) if i else None}
                 for i in range(6000)]
        result = validate_craft_document({"parts": list(reversed(parts))}, require_connected=True)
        self.assertEqual(len(result["parts"]), 6000)
        parts += [{"id": "cycle-a", "part": "fuelTankSmall", "parent_id": "cycle-b"},
                  {"id": "cycle-b", "part": "fuelTankSmall", "parent_id": "cycle-a"}]
        with self.assertRaisesRegex(CraftValidationError, "parent cycle"):
            validate_craft_document({"parts": parts}, require_connected=True)

    def test_compact_text_preserves_structured_result(self):
        message = request(1, "ksp_flight_guidance_stop")
        response = handle_message(KspMcpApplication(BlockingBridge()), message)["result"]
        self.assertEqual(json.loads(response["content"][0]["text"]), response["structuredContent"])
        self.assertNotIn("\n", response["content"][0]["text"])

    def test_notification_cannot_execute_action(self):
        bridge = BlockingBridge()
        message = request(1, "ksp_flight_stage")
        del message["id"]
        self.assertIsNone(handle_message(KspMcpApplication(bridge), message))
        self.assertEqual(bridge.commands, [])

    def test_scene_wait_uses_cached_telemetry_until_match(self):
        client = BridgeClient()
        with patch.object(client, "telemetry", side_effect=[{"scene": "MAINMENU"}, {"scene": "EDITOR"}]) as telemetry:
            with patch.object(client, "status", return_value={"scene": "EDITOR", "full": True}) as status:
                with patch("server.bridge_client.pause"):
                    result = client.wait_for_scene("editor")
        self.assertEqual(telemetry.call_count, 2)
        status.assert_called_once()
        self.assertTrue(result["full"])

    def test_batch_validates_all_items_before_sending(self):
        bridge = BlockingBridge()
        app = KspMcpApplication(bridge)
        for bad in ({"command": "batch"}, {"command": "status", "args": []}, {"command": ""}):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                app.call_tool("ksp_batch", {"commands": [{"command": "flight.stage"}, bad]})
        self.assertEqual(bridge.commands, [])
