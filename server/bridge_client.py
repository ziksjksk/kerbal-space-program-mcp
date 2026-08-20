"""HTTP client for the KSP in-game bridge."""

from __future__ import annotations

import json
import http.client
import os
import time
import threading
from urllib.parse import urlsplit, urlencode
from typing import Any


class BridgeError(RuntimeError):
    """An error returned by, or while reaching, the game plugin."""

    def __init__(self, message: str, *, code: str = "bridge_error", details: Any = None) -> None:
        super().__init__(message)
        self.code = code
        self.details = details


class BridgeClient:
    def __init__(
        self,
        base_url: str | None = None,
        token: str | None = None,
        timeout: float = 12.0,
    ) -> None:
        self.base_url = (base_url or os.environ.get("KSP_MCP_URL", "http://127.0.0.1:8765")).rstrip("/")
        self.token = token if token is not None else os.environ.get("KSP_MCP_TOKEN", "")
        self.timeout = timeout
        self._url = urlsplit(self.base_url)
        self._connection: http.client.HTTPConnection | http.client.HTTPSConnection | None = None
        self._connection_lock = threading.RLock()

    def close(self) -> None:
        """Close the reusable bridge connection, if one exists."""

        with self._connection_lock:
            if self._connection is not None:
                try:
                    self._connection.close()
                finally:
                    self._connection = None

    def _get_connection(self) -> http.client.HTTPConnection | http.client.HTTPSConnection:
        if self._connection is not None:
            return self._connection
        if self._url.scheme == "https":
            self._connection = http.client.HTTPSConnection(self._url.netloc, timeout=self.timeout)
        else:
            self._connection = http.client.HTTPConnection(self._url.netloc, timeout=self.timeout)
        return self._connection

    def _path(self, path: str) -> str:
        prefix = self._url.path.rstrip("/")
        if not path.startswith("/"):
            path = "/" + path
        return (prefix + path) or "/"

    def _request(self, method: str, path: str, payload: Any = None) -> Any:
        data = None if payload is None else json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        headers = {"Accept": "application/json", "Connection": "keep-alive"}
        if self.token:
            headers["X-KSP-MCP-Token"] = self.token
        if data is not None:
            headers["Content-Type"] = "application/json; charset=utf-8"

        with self._connection_lock:
            try:
                connection = self._get_connection()
                connection.request(method, self._path(path), body=data, headers=headers)
                response = connection.getresponse()
                raw = response.read().decode("utf-8", errors="replace")
                status = response.status
            except (TimeoutError, OSError, http.client.HTTPException) as exc:
                self.close()
                raise BridgeError(
                    f"cannot reach KSP bridge at {self.base_url}: {exc}",
                    code="not_connected",
                ) from exc

        if status >= 400:
            try:
                decoded = json.loads(raw)
            except json.JSONDecodeError:
                decoded = raw
            raise BridgeError(
                f"KSP bridge returned HTTP {status}",
                code="http_error",
                details=decoded,
            )

        try:
            envelope = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise BridgeError("KSP bridge returned invalid JSON", code="invalid_response", details=raw[:500]) from exc

        if not isinstance(envelope, dict):
            raise BridgeError("KSP bridge returned a non-object response", code="invalid_response")
        if not envelope.get("ok", False):
            error = envelope.get("error") or {}
            if isinstance(error, dict):
                raise BridgeError(
                    str(error.get("message", "unknown KSP bridge error")),
                    code=str(error.get("code", "game_error")),
                    details=error.get("details"),
                )
            raise BridgeError(str(error), code="game_error")
        return envelope.get("result")

    def status(self) -> Any:
        return self._request("GET", "/api/v1/status")

    def telemetry(
        self,
        *,
        since: int = 0,
        limit: int = 64,
        include_events: bool = True,
        wait_ms: int = 0,
    ) -> Any:
        """Read compact cached state, optionally waiting for a new event.

        ``wait_ms`` is a bounded server-side wait used by event-driven MCP
        clients.  It avoids a tight client-side poll loop while preserving the
        same event-cursor semantics as an ordinary telemetry request.
        """

        query = urlencode(
            {
                "since": max(0, int(since)),
                "limit": max(1, min(256, int(limit))),
                "include_events": "true" if include_events else "false",
                "wait_ms": max(0, min(1000, int(wait_ms))),
            }
        )
        return self._request("GET", f"/api/v1/telemetry?{query}")

    def call(self, command: str, args: dict[str, Any] | None = None) -> Any:
        return self._request(
            "POST",
            "/api/v1/command",
            {"command": command, "args": args or {}},
        )

    def call_batch(self, commands: list[dict[str, Any]]) -> Any:
        """Send several safe commands in one HTTP round trip."""

        return self.call("batch", {"commands": commands})

    def wait_for_scene(self, scene: str, timeout: float = 30.0, poll_interval: float = 0.25) -> Any:
        deadline = time.monotonic() + max(0.1, timeout)
        last_status: Any = None
        while time.monotonic() < deadline:
            last_status = self.status()
            if isinstance(last_status, dict) and str(last_status.get("scene", "")).upper() == scene.upper():
                return last_status
            time.sleep(min(max(0.05, poll_interval), 1.0))
        raise BridgeError(
            f"timed out waiting for KSP scene {scene}",
            code="timeout",
            details=last_status,
        )

