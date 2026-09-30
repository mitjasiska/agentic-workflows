"""Private acceptance, intent and irreversible publication evidence, not reports."""

from contextlib import contextmanager
import json
import os
from pathlib import Path
import tempfile

from . import TaskError
from .review_result import unique_object
from .workspace import Git


class PublicationStore:
    def __init__(self, worktree):
        self.directory = Path(Git(worktree).command("rev-parse", "--absolute-git-dir").strip())
        self.path = self.directory / "agentic-workflows-publication.json"

    @contextmanager
    def locked(self):
        """An OS lock survives neither process exit nor interruption; no stale claims."""
        path = self.directory / "agentic-workflows-publication.lock"
        try:
            fd = os.open(path, os.O_RDWR | os.O_CREAT, 0o600)
        except OSError:
            raise TaskError("Cannot lock review/publication state in the private Git directory; check filesystem access") from None
        try:
            try:
                if os.name == "nt":
                    import msvcrt
                    if os.fstat(fd).st_size == 0:
                        os.write(fd, b"\0")
                    os.lseek(fd, 0, os.SEEK_SET)
                    msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError:
                raise TaskError("Another review or publication owns this worktree; wait for it to finish") from None
            yield self
        finally:
            os.close(fd)

    def read(self):
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
            if isinstance(value, dict) and value.get("version") == 1 and set(value) == {"version", "acceptance", "intent"}:
                value.update(version=2, rebase=None)
            if isinstance(value, dict) and value.get("version") == 2 and set(value) == {"version", "acceptance", "intent", "rebase"}:
                # A legacy intent cannot prove that its push was never attempted.
                value.update(version=3, publication_history="unknown" if value["intent"] is not None else None)
            if (not isinstance(value, dict) or set(value) != {"version", "acceptance", "intent", "rebase", "publication_history"}
                    or value["version"] != 3
                    or any(value[k] is not None and not isinstance(value[k], dict) for k in ("acceptance", "intent", "rebase"))
                    or (value["publication_history"] not in (None, "unknown") and not isinstance(value["publication_history"], dict))):
                raise ValueError("invalid publication state")
            return value
        except FileNotFoundError:
            return dict(version=3, acceptance=None, intent=None, rebase=None, publication_history=None)
        except (OSError, ValueError, UnicodeError):
            raise TaskError("Cannot read publication evidence; inspect private Git metadata, never manufacture acceptance") from None

    def write(self, value):
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(dir=self.directory, mode="w", encoding="utf-8", delete=False) as output:
                temporary = Path(output.name)
                json.dump(value, output, ensure_ascii=True, sort_keys=True)
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary, self.path)
            if os.name != "nt":
                fd = os.open(self.directory, os.O_RDONLY)
                try:
                    os.fsync(fd)
                finally:
                    os.close(fd)
        except OSError:
            raise TaskError("Cannot persist publication evidence; inspect state before retrying") from None
        finally:
            if temporary is not None:
                temporary.unlink(missing_ok=True)
