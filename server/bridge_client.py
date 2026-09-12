"""HTTP client for the KSP in-game bridge."""

from __future__ import annotations

import json
import http.client
import os
import time
import threading
from urllib.parse import urlsplit, urlencode
from typing import Any

from .request_context import check_cancelled, pause


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
        # Observations must not hold the command connection during long-poll.
        self._telemetry_connection = None
        self._telemetry_lock = threading.RLock()

    def close(self) -> None:
        """Close the reusable bridge connection, if one exists."""

        for attribute, lock in (("_connection", self._connection_lock),
                                ("_telemetry_connection", self._telemetry_lock)):
            with lock:
                connection = getattr(self, attribute)
                if connection is None:
                    continue
                try:
                    connection.close()
                finally:
                    setattr(self, attribute, None)

    def _get_connection(self, attribute: str = "_connection") -> http.client.HTTPConnection | http.client.HTTPSConnection:
        connection = getattr(self, attribute)
        if connection is None:
            factory = http.client.HTTPSConnection if self._url.scheme == "https" else http.client.HTTPConnection
            connection = factory(self._url.netloc, timeout=self.timeout)
            setattr(self, attribute, connection)
        return connection

    def _path(self, path: str) -> str:
        prefix = self._url.path.rstrip("/")
        if not path.startswith("/"):
            path = "/" + path
        return (prefix + path) or "/"

    def _request(self, method: str, path: str, payload: Any = None, *, telemetry: bool = False) -> Any:
        check_cancelled()
        data = None if payload is None else json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        headers = {"Accept": "application/json", "Connection": "keep-alive"}
        # Expire queued work before the socket timeout; never replay a POST
        # whose response was lost, since the action may already have happened.
        headers["X-KSP-MCP-Timeout-Ms"] = str(max(100, int(self.timeout * 1000) - 250))
        if self.token:
            headers["X-KSP-MCP-Token"] = self.token
        if data is not None:
            headers["Content-Type"] = "application/json; charset=utf-8"

        lock = self._telemetry_lock if telemetry else self._connection_lock
        attribute = "_telemetry_connection" if telemetry else "_connection"
        with lock:
            check_cancelled()
            try:
                connection = self._get_connection(attribute)
                connection.request(method, self._path(path), body=data, headers=headers)
                response = connection.getresponse()
                raw = response.read().decode("utf-8", errors="replace")
                status = response.status
            except (TimeoutError, OSError, http.client.HTTPException) as exc:
                # Reset this lane only. Acquiring the other lane's lock here
                # would make a failed observation block controls (or deadlock).
                connection = getattr(self, attribute)
                if connection is not None:
                    connection.close()
                setattr(self, attribute, None)
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
        sections: list[str] | None = None,
    ) -> Any:
        """Read compact cached state, optionally waiting for a new event.

        ``wait_ms`` is a bounded server-side wait used by event-driven MCP
        clients.  It avoids a tight client-side poll loop while preserving the
        same event-cursor semantics as an ordinary telemetry request.
        """

        parameters = {
                "since": max(0, int(since)),
                "limit": max(1, min(256, int(limit))),
                "include_events": "true" if include_events else "false",
                "wait_ms": max(0, min(1000, int(wait_ms))),
            }
        if sections is not None:
            if not isinstance(sections, list) or any(not isinstance(section, str) or section not in {"editor", "flight", "performance"} for section in sections):
                raise ValueError("sections must be a list of editor, flight, performance")
            parameters["sections"] = ",".join(dict.fromkeys(sections))
        query = urlencode(parameters)
        return self._request("GET", f"/api/v1/telemetry?{query}", telemetry=True)

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
            check_cancelled()
            # Poll the cached snapshot, then fetch the detailed result once.
            last_status = self.telemetry(include_events=False, limit=1)
            if isinstance(last_status, dict) and str(last_status.get("scene", "")).upper() == scene.upper():
                detailed = self.status()
                if isinstance(detailed, dict) and str(detailed.get("scene", "")).upper() == scene.upper():
                    return detailed
            pause(min(max(0.05, poll_interval), 1.0, max(0.0, deadline - time.monotonic())))
        raise BridgeError(
            f"timed out waiting for KSP scene {scene}",
            code="timeout",
            details=last_status,
        )

