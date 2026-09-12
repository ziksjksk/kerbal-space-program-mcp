"""Bounded stdio scheduling: ordered commands and independent observations."""

from concurrent.futures import ThreadPoolExecutor
import json
import sys
import threading

from .request_context import RequestCancelled, cancellation_scope


# These tools only observe cached state or wait. All other tools stay in one
# FIFO executor so create/attach/stage/control commands cannot overtake each other.
OBSERVATION_TOOLS = frozenset({
    "ksp_realtime_state", "ksp_watch", "ksp_wait_for_event", "ksp_wait_for_scene",
})


def serve(application, handle, send, error, source=None):
    source = sys.stdin if source is None else source
    output_lock = threading.Lock()
    pending_lock = threading.Lock()
    pending = {}
    commands = ThreadPoolExecutor(max_workers=1, thread_name_prefix="ksp-command")
    observations = ThreadPoolExecutor(max_workers=2, thread_name_prefix="ksp-observe")
    command_slots = threading.BoundedSemaphore(32)
    observation_slots = threading.BoundedSemaphore(4)

    def emit(response):
        if response is not None:
            with output_lock:
                send(response)

    def execute(message, cancel, slots):
        try:
            with cancellation_scope(cancel):
                response = handle(application, message)
                if not cancel.is_set():
                    emit(response)
        except RequestCancelled:
            pass
        except Exception as exc:
            if not cancel.is_set():
                emit(error(message["id"], -32603, "internal error", str(exc)))
        finally:
            with pending_lock:
                pending.pop(message["id"], None)
            slots.release()

    try:
        for line in source:
            if not line.strip():
                continue
            try:
                message = json.loads(line)
            except json.JSONDecodeError as exc:
                emit(error(None, -32700, "parse error", str(exc)))
                continue
            # Let the protocol handler reject malformed envelopes before any
            # work is admitted. Notifications never invoke game commands.
            if (not isinstance(message, dict) or message.get("jsonrpc") != "2.0"
                    or not isinstance(message.get("method"), str)
                    or not isinstance(message.get("params", {}), dict)
                    or ("id" in message and
                        (isinstance(message["id"], bool) or not isinstance(message["id"], (str, int))))):
                emit(handle(application, message))
                continue
            if "id" not in message:
                if message["method"] == "notifications/cancelled":
                    request_id = message.get("params", {}).get("requestId")
                    if isinstance(request_id, (str, int)) and not isinstance(request_id, bool):
                        with pending_lock:
                            cancel = pending.get(request_id)
                            if cancel is not None:
                                cancel.set()
                continue
            request_id = message["id"]
            with pending_lock:
                duplicate = request_id in pending
            if duplicate:
                emit(error(request_id, -32600, "request id is already in progress"))
                continue
            if message["method"] != "tools/call":
                emit(handle(application, message))
                continue
            name = message.get("params", {}).get("name")
            observe = isinstance(name, str) and name in OBSERVATION_TOOLS
            executor = observations if observe else commands
            slots = observation_slots if observe else command_slots
            if not slots.acquire(blocking=False):
                emit(error(request_id, -32000, "server busy", {"retryable": True}))
                continue
            cancel = threading.Event()
            with pending_lock:
                pending[request_id] = cancel
            executor.submit(execute, message, cancel, slots)
    finally:
        # EOF is a disconnect. Stop observing and skip commands not yet sent;
        # an already dispatched game action cannot be rolled back by cancelling.
        with pending_lock:
            for cancel in pending.values():
                cancel.set()
        observations.shutdown(wait=True)
        commands.shutdown(wait=True)
        close = getattr(application.bridge, "close", None)
        if close is not None:
            close()
