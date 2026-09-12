"""Cooperative cancellation at safe request and observation boundaries."""

from contextlib import contextmanager
import threading
import time


class RequestCancelled(Exception):
    pass


_local = threading.local()


def check_cancelled():
    event = getattr(_local, "cancel", None)
    if event is not None and event.is_set():
        raise RequestCancelled("request cancelled")


def pause(seconds):
    check_cancelled()
    if seconds <= 0:
        return
    event = getattr(_local, "cancel", None)
    if event is None:
        time.sleep(max(0.0, seconds))
    elif event.wait(max(0.0, seconds)):
        raise RequestCancelled("request cancelled")


@contextmanager
def cancellation_scope(event):
    previous = getattr(_local, "cancel", None)
    _local.cancel = event
    try:
        check_cancelled()
        yield
    finally:
        _local.cancel = previous
