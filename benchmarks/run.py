"""Repeatable local efficiency measurements; the HTTP fixture is NOT KSP.

Run against another checkout with --root PATH. Timings are informational,
not pass/fail thresholds. No game commands or installed game files are used.
"""

import argparse
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import queue
import statistics
import subprocess
import sys
import threading
import time


def median_ms(action, repeats=5):
    samples = []
    for _ in range(repeats):
        start = time.perf_counter()
        action()
        samples.append((time.perf_counter() - start) * 1000)
    return round(statistics.median(samples), 4)


class FixtureHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def respond(self, value):
        payload = json.dumps({"ok": True, "result": value}).encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        if "wait_ms=" in self.path and "wait_ms=0" not in self.path:
            self.server.observing.set()
            time.sleep(0.25)
        self.respond({"scene": "EDITOR", "event_cursor": 0, "events": []})

    def do_POST(self):
        self.rfile.read(int(self.headers["Content-Length"]))
        self.respond({"ok": True})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    sys.path.insert(0, str(root))
    from server.bridge_client import BridgeClient
    from server.craft_model import validate_craft_document
    from server.mcp_server import KspMcpApplication, _json_line, handle_message

    results = {"python": sys.version.split()[0], "platform": sys.platform,
               "fixture": "synthetic stock-like craft; loopback HTTP with 250ms event wait; no game"}
    validation = {}
    for count in (100, 1000, 5000):
        craft = {"parts": [{"id": str(i), "part": "fuelTankSmall",
                             "parent_id": str(i-1) if i else None,
                             "parent_attach_node": "bottom" if i else None,
                             "attach_node": "top" if i else None}
                            for i in range(count)]}
        validation[str(count)] = median_ms(lambda: validate_craft_document(craft, require_connected=True))
    results["craft_validation_median_ms"] = validation

    sample = {"scene": "FLIGHT", "event_cursor": 42,
              "flight": {"altitude": 12345.67, "apoapsis": 80000, "periapsis": 75000,
                         "guidance": {"active": True, "phase": "circularisation"},
                         "resources": [{"name": "LiquidFuel", "amount": 20.5, "capacity": 100}]},
              "events": [{"event_id": i, "type": "editor.build.part_added",
                          "data": {"part_id": str(i), "parent_id": str(i-1), "total": 100}}
                         for i in range(1, 65)]}
    fixture = {"samples": [dict(sample, sequence=i) for i in range(20)], "sample_count": 20}

    class FixtureBridge:
        def status(self):
            return fixture

    application = KspMcpApplication(FixtureBridge())
    request = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "ksp_status"}}
    serialize = lambda: _json_line(handle_message(application, request))
    results["mcp_serialization_median_ms"] = median_ms(serialize, 20)
    results["mcp_response_bytes"] = len(serialize().encode("utf-8"))

    httpd = ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
    httpd.observing = threading.Event()
    listener = threading.Thread(target=httpd.serve_forever, daemon=True)
    listener.start()
    url = "http://127.0.0.1:" + str(httpd.server_port)
    client = BridgeClient(url)
    try:
        command_times = []
        with ThreadPoolExecutor(max_workers=1) as pool:
            for _ in range(5):
                httpd.observing.clear()
                observation = pool.submit(client.telemetry, wait_ms=1000)
                if not httpd.observing.wait(3):
                    raise RuntimeError("HTTP fixture was not reached")
                start = time.perf_counter()
                client.call("flight.guidance_stop")
                command_times.append((time.perf_counter() - start) * 1000)
                observation.result(timeout=3)
        results["http_control_during_wait_median_ms"] = round(statistics.median(command_times), 4)

        stdio_times = []
        for _ in range(3):
            httpd.observing.clear()
            env = dict(os.environ, KSP_MCP_URL=url, KSP_MCP_TOKEN="", PYTHONIOENCODING="utf-8")
            process = subprocess.Popen([sys.executable, "-m", "server"], cwd=root, env=env,
                                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       text=True, encoding="utf-8")
            replies = queue.Queue()
            reader = threading.Thread(target=lambda: [replies.put(json.loads(line)) for line in process.stdout], daemon=True)
            reader.start()
            try:
                process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                                                "params": {"name": "ksp_wait_for_event", "arguments": {"timeout": 0.4}}}) + "\n")
                process.stdin.flush()
                if not httpd.observing.wait(3):
                    raise RuntimeError("stdio observation did not start")
                start = time.perf_counter()
                process.stdin.write('{"jsonrpc":"2.0","id":2,"method":"ping"}\n')
                process.stdin.flush()
                while replies.get(timeout=3)["id"] != 2:
                    pass
                stdio_times.append((time.perf_counter() - start) * 1000)
            finally:
                process.stdin.close()
                try:
                    process.wait(timeout=4)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                reader.join(2)
                process.stdout.close()
                process.stderr.close()
        results["stdio_ping_during_wait_median_ms"] = round(statistics.median(stdio_times), 4)
    finally:
        client.close()
        httpd.shutdown()
        httpd.server_close()
        listener.join(3)
    text = json.dumps(results, indent=2, ensure_ascii=False)
    print(text)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
