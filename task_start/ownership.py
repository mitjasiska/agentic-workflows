"""Machine-local exclusion between workflow controllers and destructive disposal.

Shared holders may operate concurrently. Cleanup takes the exclusive side before
reading ownership, and keeps it through retirement. Durable reservations still
come from disposal journals, not this process-lifetime lock.
"""

from contextlib import contextmanager
from functools import wraps
from inspect import signature
import os
from pathlib import Path
import threading

from . import TaskError


_held = threading.local()


@contextmanager
def ownership_gate(*, exclusive=False, registry=None):
    from .contexts import registry_path
    path = (Path(registry) if registry is not None else registry_path()).parent / "ownership.lock"
    path = path.absolute()
    key = (os.getpid(), str(path))
    held = getattr(_held, "locks", None)
    if held is None:
        held = _held.locks = {}
    if key in held:
        if exclusive and not held[key]:
            raise TaskError("Cannot begin disposal inside an active workflow controller")
        yield
        return
    fd = None
    try:
        path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        if path.resolve() != path:
            raise TaskError("Workflow ownership lock path is aliased")
        fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        import fcntl
        try:
            fcntl.flock(fd, (fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH) | fcntl.LOCK_NB)
        except BlockingIOError:
            raise TaskError("Workflow ownership is busy: another controller or cleanup is active; retry after it stops") from None
        held[key] = exclusive
        try:
            yield
        finally:
            del held[key]
    except OSError:
        raise TaskError("Cannot lock machine-local workflow ownership; no mutation is safe") from None
    finally:
        if fd is not None:
            os.close(fd)


def ownership_operation(function=None, *, exclusive=False):
    def decorate(fn):
        parameters = signature(fn)
        @wraps(fn)
        def guarded(*args, **kwargs):
            registry = parameters.bind_partial(*args, **kwargs).arguments.get("registry")
            with ownership_gate(exclusive=exclusive, registry=registry.path if registry is not None else None):
                return fn(*args, **kwargs)
        return guarded
    return decorate(function) if function is not None else decorate
