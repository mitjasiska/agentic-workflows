"""Read-only local process evidence for explicit integration abandonment."""

import os
from pathlib import Path
import sys

from . import TaskError


def stopped_checkout(path, shell_pid=None, *, proc=Path("/proc")):
    """Require no checkout processes or shell children, including background jobs.

    Herdr foreground status alone cannot establish this. Unsupported platforms or
    inaccessible same-user process evidence refuse instead of assuming absence.
    """
    if not sys.platform.startswith("linux") or not proc.is_dir():
        raise TaskError("Integration abandonment requires readable local Linux process evidence")
    processes = {}
    try:
        for entry in proc.iterdir():
            if not entry.name.isdecimal():
                continue
            try:
                if entry.stat().st_uid != os.getuid():
                    continue
                fields = (entry / "stat").read_text().rsplit(")", 1)[1].split()
                if fields[0] == "Z":
                    continue
                parent, started = int(fields[1]), int(fields[19])
                cwd = Path(os.readlink(entry / "cwd"))
                argv = os.fsdecode((entry / "cmdline").read_bytes())
                processes[int(entry.name)] = (parent, started, cwd, argv)
            except FileNotFoundError:
                if entry.exists():
                    raise  # Missing evidence for a still-present process is uncertain.
        if shell_pid is not None:
            shell = processes.get(shell_pid)
            if shell is None or shell[2] != path:
                raise TaskError("Integration shell process is absent or changed; abandonment refused. "
                                "Run recovery on the same host/PID namespace as Herdr and inspect the exact shell")
        descendants = {shell_pid} if shell_pid is not None else set()
        while True:
            found = {pid for pid, value in processes.items() if value[0] in descendants}
            if found <= descendants:
                break
            descendants |= found
        for pid, (_, _, cwd, argv) in processes.items():
            if pid != shell_pid and (pid in descendants or cwd.is_relative_to(path) or str(path) in argv):
                raise TaskError("A process may still use the integration checkout; abandonment refused")
        return dict(pid=shell_pid, started=processes[shell_pid][1] if shell_pid is not None else None)
    except (OSError, ValueError, IndexError):
        raise TaskError("Cannot prove integration processes have stopped; abandonment refused") from None
