import unittest

from server.mcp_server import KspMcpApplication, TOOLS, handle_message


class FakeBridge:
    def status(self):
        return {"scene": "EDITOR", "ok": True}

    def call(self, command, args):
        return {"command": command, "args": args}

    def wait_for_scene(self, scene, timeout):
        return {"scene": scene, "timeout": timeout}


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


if __name__ == "__main__":
    unittest.main()
