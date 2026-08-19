"""HTTP client for the KSP in-game bridge."""

from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request
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

    def _request(self, method: str, path: str, payload: dict[str, Any] | None = None) -> Any:
        url = f"{self.base_url}{path}"
        data = None
        headers = {"Accept": "application/json"}
        if self.token:
            headers["X-KSP-MCP-Token"] = self.token
        if payload is not None:
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json; charset=utf-8"
        request = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                raw = response.read().decode("utf-8")
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            try:
                decoded = json.loads(body)
            except json.JSONDecodeError:
                decoded = body
            raise BridgeError(
                f"KSP bridge returned HTTP {exc.code}",
                code="http_error",
                details=decoded,
            ) from exc
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise BridgeError(
                f"cannot reach KSP bridge at {self.base_url}: {exc}",
                code="not_connected",
            ) from exc

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

    def call(self, command: str, args: dict[str, Any] | None = None) -> Any:
        return self._request(
            "POST",
            "/api/v1/command",
            {"command": command, "args": args or {}},
        )

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
