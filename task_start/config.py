from dataclasses import dataclass, field
from pathlib import Path
import re
import tomllib
from typing import Mapping

from . import TaskError

REGISTRY = Path(__file__).resolve().parent.parent / "config" / "projects.toml"


@dataclass(frozen=True)
class Project:
    linear_project: str
    repo_name: str
    base_branch: str


@dataclass(frozen=True)
class AgentConfig:
    kind: str
    model: str | None = None
    mode: str | None = None

    @property
    def reasoning(self) -> str | None:
        """Compatibility name for configurations written before workflow modes."""
        return self.mode


@dataclass(frozen=True)
class LocalConfig:
    projects_root: Path
    api_key: str = field(repr=False)
    agent: AgentConfig | None = None
    codex_repository_profiles: Mapping[str, str] = field(default_factory=dict)


def read_toml(path: Path) -> dict:
    try:
        with path.open("rb") as source:
            return tomllib.load(source)
    except FileNotFoundError:
        raise TaskError(f"Config missing: {path}") from None
    except (OSError, UnicodeError, tomllib.TOMLDecodeError):
        # Never echo TOML contents: this file can contain credentials.
        raise TaskError(f"Cannot read valid TOML from {path}") from None


def required_text(data: dict, key: str) -> str:
    value = data.get(key) if isinstance(data, dict) else None
    if not isinstance(value, str) or not value.strip():
        raise TaskError(f"Config requires a nonempty {key}")
    return value.strip()


def load_local(path: Path | None = None, *, no_agent: bool = False) -> LocalConfig:
    data = read_toml(path or Path.home() / ".agentic-workflows" / "config.toml")
    api_key = required_text(data.get("linear"), "api_key")
    if any(char.isspace() for char in api_key):
        raise TaskError("Linear api_key must not contain whitespace")
    try:
        root = Path(required_text(data, "projects_root")).expanduser()
        if not root.is_absolute():
            raise TaskError("projects_root must be absolute (or start with ~)")
        agent = None if no_agent else agent_config(data.get("agent"))
        profiles = {} if no_agent else codex_repository_profiles(data.get("codex"))
        return LocalConfig(root.resolve(), api_key, agent, profiles)
    except (OSError, ValueError, RuntimeError):
        raise TaskError("projects_root could not be resolved") from None


def agent_config(data: dict | None) -> AgentConfig | None:
    # Older configurations remain useful for --no-agent.
    if data is None:
        return None
    kind = required_text(data, "kind")
    if not re.fullmatch(r"[a-z][a-z0-9_-]*", kind):
        raise TaskError("agent.kind must be a lowercase agent name such as codex or pi")

    model = data.get("model")
    if model is not None:
        if (not isinstance(model, str) or not model.strip()
                or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/@*+-]*", model.strip())):
            raise TaskError("agent.model must be a model name without whitespace or control characters")
        model = model.strip()

    # `reasoning` was the original Codex-shaped field. `mode` is the
    # agent-independent spelling; accepting exactly one makes migration explicit.
    if "mode" in data and "reasoning" in data:
        raise TaskError("[agent] must use either mode or legacy reasoning, not both")
    mode_key = "mode" if "mode" in data else "reasoning"
    mode = data.get(mode_key)
    if mode is not None:
        if (not isinstance(mode, str) or not mode.strip()
                or not re.fullmatch(r"[a-z][a-z0-9_-]*", mode.strip())):
            raise TaskError(f"agent.{mode_key} must be a lowercase execution mode without whitespace")
        mode = mode.strip()
    return AgentConfig(kind, model, mode)


def codex_repository_profiles(data: dict | None) -> dict[str, str]:
    """Read exact repository-to-profile selections from machine-local config."""
    if data is None:
        return {}
    if not isinstance(data, dict) or set(data) - {"repositories"}:
        raise TaskError("[codex] supports only the repositories table")
    repositories = data.get("repositories", {})
    if not isinstance(repositories, dict):
        raise TaskError("codex.repositories must be a table")

    profiles = {}
    for repository, override in repositories.items():
        if (not isinstance(repository, str)
                or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", repository)):
            raise TaskError("codex repository names must be portable directory names")
        if not isinstance(override, dict) or set(override) != {"profile"}:
            raise TaskError(f"codex.repositories.{repository} requires only profile")
        profile = required_text(override, "profile")
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", profile):
            raise TaskError("Codex profile must be a portable name without path or option syntax")
        profiles[repository] = profile
    return profiles


def load_projects(path: Path = REGISTRY) -> list[Project]:
    entries = read_toml(path).get("projects")
    if not isinstance(entries, dict) or not entries:
        raise TaskError("Project registry requires a [projects] table")
    projects = []
    for entry in entries.values():
        project = Project(*(required_text(entry, key) for key in (
            "linear_project", "repo_name", "base_branch"
        )))
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", project.repo_name):
            raise TaskError("repo_name must be a single portable directory name")
        projects.append(project)
    if len({p.linear_project for p in projects}) != len(projects):
        raise TaskError("Duplicate linear_project names in project registry")
    return projects


def resolve_project(projects: list[Project], name: str) -> Project:
    matches = [p for p in projects if p.linear_project == name]
    if len(matches) != 1:
        raise TaskError(f"Linear project {name!r} must match exactly one configured project")
    return matches[0]


def repository_path(local: LocalConfig, project: Project) -> Path:
    return (local.projects_root / project.repo_name).resolve()
