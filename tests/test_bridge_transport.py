from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import unittest
from urllib.parse import parse_qs, urlsplit

from server.bridge_client import BridgeClient, BridgeError


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def respond(self, value, status=200):
        data = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        query = parse_qs(urlsplit(self.path).query, keep_blank_values=True)
        with self.server.guard:
            self.server.reads.append((self.client_address, query))
        if int(query.get("wait_ms", [0])[0]) > 0:
            self.server.observing.set()
            self.server.release.wait(2)
        result = {"scene": "EDITOR", "event_cursor": 0, "events": [], "name": "测试火箭"}
        self.respond({"ok": True, "result": result})

    def do_POST(self):
        data = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        with self.server.guard:
            self.server.commands.append(data)
        if data["command"] == "fail":
            self.respond({"ok": False, "error": {"code": "busy"}}, 503)
        else:
            self.respond({"ok": True, "result": data})


class BridgeTransportTests(unittest.TestCase):
    def setUp(self):
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.server.guard = threading.Lock()
        self.server.commands = []
        self.server.reads = []
        self.server.observing = threading.Event()
        self.server.release = threading.Event()
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = "http://127.0.0.1:" + str(self.server.server_port)
        self.client = BridgeClient(self.url, timeout=3)

    def tearDown(self):
        self.server.release.set()
        self.client.close()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(3)

    def test_long_poll_does_not_lock_command_connection(self):
        with ThreadPoolExecutor(max_workers=2) as pool:
            observation = pool.submit(self.client.telemetry, wait_ms=1000)
            self.assertTrue(self.server.observing.wait(2))
            try:
                command = pool.submit(self.client.call, "flight.guidance_stop")
                result = command.result(timeout=1)
                self.assertEqual(result["command"], "flight.guidance_stop")
                self.assertFalse(observation.done())
            finally:
                self.server.release.set()
            self.assertEqual(observation.result(timeout=2)["scene"], "EDITOR")

    def test_connections_reused_and_sections_encoded(self):
        self.client.telemetry(sections=[])
        self.client.telemetry(sections=["flight", "flight", "performance"])
        self.assertEqual(self.server.reads[0][0], self.server.reads[1][0])
        self.assertEqual(self.server.reads[0][1]["sections"], [""])
        self.assertEqual(self.server.reads[1][1]["sections"], ["flight,performance"])
        self.assertEqual(self.client.status()["name"], "测试火箭")
        self.assertNotEqual(self.server.reads[0][0], self.server.reads[-1][0])

    def test_failed_post_is_not_retried(self):
        with self.assertRaises(BridgeError):
            self.client.call("fail")
        self.assertEqual(len(self.server.commands), 1)
        self.assertEqual(self.client.call("flight.guidance_stop")["command"], "flight.guidance_stop")

    def test_invalid_sections_rejected_before_network(self):
        for sections in ("flight", [None], ["unknown"], [{}]):
            with self.subTest(sections=sections), self.assertRaises(ValueError):
                self.client.telemetry(sections=sections)
        self.assertEqual(self.server.reads, [])

    def test_real_stdio_process_responds_during_http_wait(self):
        env = dict(os.environ, KSP_MCP_URL=self.url, KSP_MCP_TOKEN="")
        process = subprocess.Popen([sys.executable, "-m", "server"],
                                   cwd=Path(__file__).resolve().parents[1], env=env,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, encoding="utf-8", bufsize=1)
        replies = queue.Queue()
        reader = threading.Thread(target=lambda: [replies.put(json.loads(line)) for line in process.stdout], daemon=True)
        reader.start()

        def send(value):
            process.stdin.write(json.dumps(value) + "\n")
            process.stdin.flush()

        try:
            send({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                  "params": {"name": "ksp_wait_for_event", "arguments": {"timeout": 30}}})
            self.assertTrue(self.server.observing.wait(3))
            send({"jsonrpc": "2.0", "id": 2, "method": "ping"})
            self.assertEqual(replies.get(timeout=1)["id"], 2)
            send({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
                  "params": {"name": "ksp_flight_guidance_stop", "arguments": {}}})
            self.assertEqual(replies.get(timeout=1)["id"], 3)
            send({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
                  "params": {"name": "ksp_status", "arguments": {}}})
            response = replies.get(timeout=1)
            self.assertEqual(response["result"]["structuredContent"]["name"], "测试火箭")
            send({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 1}})
        finally:
            self.server.release.set()
            process.stdin.close()
            try:
                process.wait(timeout=4)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            reader.join(2)
            stderr = process.stderr.read()
            process.stdout.close()
            process.stderr.close()
        self.assertEqual(process.returncode, 0, stderr)
