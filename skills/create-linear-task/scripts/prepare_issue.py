#!/usr/bin/env python3
"""Validate and render an agent-ready Linear issue from semantic draft JSON."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
from pathlib import Path
import re
import subprocess
import sys
import tomllib
from typing import Any


DEFAULT_CONFIG = Path(__file__).resolve().parents[1] / "config.toml"
COLLAPSE_MARKER = re.compile(r"(?m)^[ \t]*\+\+\+(?:[ \t].*)?$")
CANONICAL_TASK_KINDS = ("implementation", "research", "experiment")
CANONICAL_PRIMARY = {
    "feature": ("Feature", "feat"),
    "bug": ("Bug", "fix"),
    "chore": ("Chore", "chore"),
    "docs": ("Docs", "docs"),
    "refactor": ("Refactor", "refactor"),
}
CANONICAL_RESEARCH_LABEL = "Research"


class PreparationError(ValueError):
    """Raised when configuration or draft input is unsafe or ambiguous."""


@dataclass(frozen=True)
class PrimaryCategory:
    linear_label: str
    change_type: str


@dataclass(frozen=True)
class Settings:
    task_kinds: tuple[str, ...]
    primary_categories: dict[str, PrimaryCategory]
    research_label: str
    stop_condition: str
    projects: dict[str, "ProjectTarget"]


@dataclass(frozen=True)
class ProjectTarget:
    key: str
    repo_name: str
    linear_project: str
    linear_team: str | None
    replace_workflow_labels: bool


@dataclass(frozen=True)
class Label:
    id: str
    name: str


def _nonempty_text(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise PreparationError(f"{field} must be nonempty text")
    result = value.strip()
    if "\0" in result:
        raise PreparationError(f"{field} must not contain a NUL character")
    if COLLAPSE_MARKER.search(result):
        raise PreparationError(f"{field} must not contain a Linear collapse marker")
    return result


def _string_list(value: Any, field: str, *, required: bool = False) -> list[str]:
    if not isinstance(value, list):
        raise PreparationError(f"{field} must be a list")
    result = [_nonempty_text(item, f"{field} item") for item in value]
    if required and not result:
        raise PreparationError(f"{field} must contain at least one item")
    if len(set(result)) != len(result):
        raise PreparationError(f"{field} must not contain duplicates")
    return result


def _exact_keys(value: dict, expected: set[str], field: str) -> None:
    actual = set(value)
    if actual != expected:
        missing = sorted(expected - actual)
        unknown = sorted(actual - expected)
        details = []
        if missing:
            details.append(f"missing {missing!r}")
        if unknown:
            details.append(f"unknown {unknown!r}")
        raise PreparationError(f"{field} has invalid fields: {', '.join(details)}")


def load_settings(path: Path) -> Settings:
    try:
        with path.open("rb") as source:
            raw = tomllib.load(source)
    except (OSError, tomllib.TOMLDecodeError) as error:
        raise PreparationError(f"cannot read valid task configuration: {path}") from error

    creation = raw.get("task_creation")
    projects = raw.get("projects")
    if not isinstance(creation, dict) or not isinstance(projects, dict) or not projects:
        raise PreparationError("configuration requires [task_creation] and [projects]")
    _exact_keys(
        creation,
        {"task_kinds", "default_stop_condition", "primary_categories", "research_modifier"},
        "task_creation",
    )
    task_kinds = tuple(_string_list(creation["task_kinds"], "task_creation.task_kinds", required=True))
    if task_kinds != CANONICAL_TASK_KINDS:
        raise PreparationError(
            f"task_creation.task_kinds must be exactly {list(CANONICAL_TASK_KINDS)!r}"
        )
    stop_condition = _nonempty_text(
        creation["default_stop_condition"], "task_creation.default_stop_condition"
    )

    raw_primary = creation["primary_categories"]
    if not isinstance(raw_primary, dict):
        raise PreparationError("task_creation.primary_categories must be a table")
    if set(raw_primary) != set(CANONICAL_PRIMARY):
        raise PreparationError(
            "primary categories must be exactly feature, bug, chore, docs, and refactor"
        )
    primary_categories: dict[str, PrimaryCategory] = {}
    for category, (expected_label, expected_type) in CANONICAL_PRIMARY.items():
        entry = raw_primary[category]
        if not isinstance(entry, dict):
            raise PreparationError(f"primary category {category} must be a table")
        _exact_keys(entry, {"linear_label", "change_type"}, f"primary category {category}")
        label = _nonempty_text(entry["linear_label"], f"label for {category}")
        change_type = _nonempty_text(entry["change_type"], f"change type for {category}")
        if (label, change_type) != (expected_label, expected_type):
            raise PreparationError(
                f"primary category {category} must map to {expected_label!r} and {expected_type!r}"
            )
        primary_categories[category] = PrimaryCategory(label, change_type)

    research = creation["research_modifier"]
    if not isinstance(research, dict):
        raise PreparationError("task_creation.research_modifier must be a table")
    _exact_keys(research, {"linear_label"}, "task_creation.research_modifier")
    research_label = _nonempty_text(
        research["linear_label"], "task_creation.research_modifier.linear_label"
    )
    if research_label != CANONICAL_RESEARCH_LABEL:
        raise PreparationError("the research modifier must map to the 'Research' label")

    targets: dict[str, ProjectTarget] = {}
    repo_names: set[str] = set()
    for key, value in projects.items():
        if not isinstance(key, str) or not isinstance(value, dict):
            raise PreparationError("each project must be a named table")
        repo_name = _nonempty_text(value.get("repo_name"), f"projects.{key}.repo_name")
        if repo_name in repo_names:
            raise PreparationError(f"multiple projects use repo_name {repo_name!r}")
        repo_names.add(repo_name)
        project = _nonempty_text(value.get("linear_project"), f"projects.{key}.linear_project")
        team_value = value.get("linear_team")
        team = None if team_value is None else _nonempty_text(team_value, f"projects.{key}.linear_team")
        replace = value.get("replace_workflow_labels", False)
        if not isinstance(replace, bool):
            raise PreparationError(f"projects.{key}.replace_workflow_labels must be true or false")
        targets[key] = ProjectTarget(key, repo_name, project, team, replace)
    return Settings(task_kinds, primary_categories, research_label, stop_condition, targets)


def _repository_names(repository: Path) -> set[str]:
    path = repository.resolve()
    names = {path.name}
    try:
        result = subprocess.run(
            ["git", "-C", str(path), "rev-parse", "--path-format=absolute", "--git-common-dir"],
            text=True,
            capture_output=True,
            check=True,
            timeout=10,
        )
        common = Path(result.stdout.strip())
        if common.name == ".git" and common.is_absolute():
            names.add(common.parent.name)
    except (OSError, subprocess.SubprocessError):
        # A non-Git directory can still be selected by its exact configured name.
        pass
    return names


def resolve_target(settings: Settings, repository: Path, project_key: str | None) -> ProjectTarget:
    repo_names = _repository_names(repository)
    if project_key is not None:
        target = settings.projects.get(project_key)
        if target is None:
            raise PreparationError(f"project key {project_key!r} is not configured")
        if target.repo_name not in repo_names:
            raise PreparationError(
                f"project {project_key!r} is for repository {target.repo_name!r}, "
                f"not one of {sorted(repo_names)!r}"
            )
    else:
        matches = [target for target in settings.projects.values() if target.repo_name in repo_names]
        if len(matches) != 1:
            raise PreparationError(
                f"repository identities {sorted(repo_names)!r} must match exactly one configured project"
            )
        target = matches[0]
    if target.linear_team is None:
        raise PreparationError(f"project {target.key!r} has no configured linear_team")
    return target


def _optional_text(value: Any, field: str) -> str | None:
    if value is None:
        return None
    return _nonempty_text(value, field)


def _labels(value: Any, field: str) -> list[Label]:
    if not isinstance(value, list):
        raise PreparationError(f"{field} must be a complete list of label objects")
    result: list[Label] = []
    for index, item in enumerate(value):
        if not isinstance(item, dict):
            raise PreparationError(f"{field}[{index}] must be an object")
        _exact_keys(item, {"id", "name"}, f"{field}[{index}]")
        result.append(Label(
            _nonempty_text(item["id"], f"{field}[{index}].id"),
            _nonempty_text(item["name"], f"{field}[{index}].name"),
        ))
    if len({label.id for label in result}) != len(result):
        raise PreparationError(f"{field} contains duplicate label IDs")
    return result


def _bullet_list(items: list[str]) -> str:
    lines: list[str] = []
    for item in items:
        parts = item.splitlines()
        lines.append(f"- {parts[0]}")
        lines.extend(f"  {part}" for part in parts[1:])
    return "\n".join(lines)


def _agent_section(title: str, values: list[str]) -> list[str]:
    return [] if not values else [f"### {title}", "", _bullet_list(values), ""]


def render_description(
    *,
    context: str | None,
    outcome: str,
    done_when: list[str],
    boundaries: list[str],
    agent: dict[str, list[str]],
    task_kind: str | None,
    primary_category: str | None,
    research_modifier: bool,
    intended_labels: list[str],
    change_type: str | None,
    stop_condition: str,
) -> str:
    parts: list[str] = []
    if context is not None:
        parts.extend(["## Context / why", "", context, ""])
    parts.extend(["## Scope / outcome", "", outcome, "", "## Done when", "", _bullet_list(done_when), ""])
    if boundaries:
        parts.extend(["## Boundaries", "", _bullet_list(boundaries), ""])
    # Linear's API Markdown uses +++ delimiters for collapsible sections.
    # The >>> form is only an interactive-editor shortcut and is not submitted here.
    parts.extend([
        "+++ Agent instructions",
        "",
        "These instructions are addressed to the implementation agent executing this issue. Treat them as instructions to you.",
        "",
    ])
    parts.extend(_agent_section("Repository / context", agent["repository_context"]))
    parts.extend(_agent_section("Implementation / research guidance", agent["guidance"]))
    parts.extend(_agent_section("Constraints", agent["constraints"]))
    parts.extend(_agent_section("Validation", agent["validation"]))
    label_metadata = ", ".join(f"`{label}`" for label in intended_labels) or "`none`"
    parts.extend(["### Stop condition", "", stop_condition, "", "### Workflow metadata", ""])
    parts.extend([
        f"- Task kind: `{task_kind or 'unclassified'}`",
        f"- Primary category: `{primary_category or 'unclassified'}`",
        f"- Research modifier: `{'yes' if research_modifier else 'no'}`",
        f"- Intended workflow labels: {label_metadata}",
        f"- Change type: `{change_type or 'none'}`",
        "",
        "+++",
    ])
    return "\n".join(parts).rstrip() + "\n"


def _resolve_label(catalog: list[Label], name: str) -> Label:
    matches = [label for label in catalog if label.name == name]
    if len(matches) != 1:
        state = "missing" if not matches else "ambiguous"
        raise PreparationError(f"canonical Linear label {name!r} is {state}; do not substitute another label")
    return matches[0]


def _classification(settings: Settings, draft: dict) -> tuple[str | None, str | None, bool]:
    required = {"task_kind", "primary_category", "research_modifier"}
    missing = required - set(draft)
    if missing:
        raise PreparationError(
            f"draft is missing required classification fields: {sorted(missing)!r}"
        )
    task_kind = draft["task_kind"]
    if task_kind is not None and task_kind not in settings.task_kinds:
        raise PreparationError(
            f"task_kind must be one of {', '.join(settings.task_kinds)} or null; "
            "clarify instead of guessing"
        )
    primary_category = draft["primary_category"]
    if primary_category is not None and not isinstance(primary_category, str):
        raise PreparationError("primary_category must contain at most one category or null")
    if primary_category is not None and primary_category not in settings.primary_categories:
        raise PreparationError(
            "primary_category must be feature, bug, chore, docs, refactor, or null; "
            "do not invent categories"
        )
    research_modifier = draft["research_modifier"]
    if not isinstance(research_modifier, bool):
        raise PreparationError("research_modifier must be true or false")
    if task_kind == "research" and not research_modifier:
        raise PreparationError(
            "task_kind 'research' requires research_modifier true"
        )
    return task_kind, primary_category, research_modifier


def _label_snapshot(draft: dict) -> tuple[list[Label], list[Label]]:
    existing_labels = _labels(draft.get("existing_labels"), "existing_labels")
    workspace_labels = _labels(draft.get("workspace_labels"), "workspace_labels")
    return existing_labels, workspace_labels


def _intended_workflow(
    settings: Settings, primary_category: str | None, research_modifier: bool
) -> tuple[list[str], str | None]:
    intended_labels: list[str] = []
    change_type = None
    if primary_category is not None:
        primary = settings.primary_categories[primary_category]
        intended_labels.append(primary.linear_label)
        change_type = primary.change_type
    if research_modifier:
        intended_labels.append(settings.research_label)
    return intended_labels, change_type


def _workflow_names(settings: Settings) -> set[str]:
    return {
        category.linear_label for category in settings.primary_categories.values()
    } | {settings.research_label}


def _workflow_catalog(settings: Settings, catalog: list[Label]) -> dict[str, Label]:
    """Resolve the complete closed taxonomy before any issue mutation is prepared."""
    return {
        name: _resolve_label(catalog, name)
        for name in sorted(_workflow_names(settings))
    }


def _target_metadata(
    target: ProjectTarget, draft: dict, *, require_existing: bool = False
) -> dict[str, Any]:
    if "existing_target" not in draft:
        raise PreparationError(
            "draft is missing required existing_target field; use null for a new issue"
        )
    existing = draft["existing_target"]
    if existing is None:
        if require_existing:
            raise PreparationError(
                "post-mutation verification requires the issue's current existing_target"
            )
        return {
            "source": "configuration",
            "project_key": target.key,
            "team": target.linear_team,
            "project": target.linear_project,
        }
    if not isinstance(existing, dict):
        raise PreparationError("existing_target must be an object or null")
    _exact_keys(
        existing,
        {"team_id", "team_name", "project_id", "project_name"},
        "existing_target",
    )
    team_id = _nonempty_text(existing["team_id"], "existing_target.team_id")
    team_name = _nonempty_text(existing["team_name"], "existing_target.team_name")
    project_id = _nonempty_text(existing["project_id"], "existing_target.project_id")
    project_name = _nonempty_text(existing["project_name"], "existing_target.project_name")
    if team_name != target.linear_team or project_name != target.linear_project:
        raise PreparationError(
            "existing issue target conflicts with configured target; refusing to move it: "
            f"expected team {target.linear_team!r} and project {target.linear_project!r}, "
            f"found team {team_name!r} and project {project_name!r}"
        )
    return {
        "source": "existing_issue",
        "project_key": target.key,
        "team": team_name,
        "project": project_name,
        "team_id": team_id,
        "project_id": project_id,
    }


def _verify_target_metadata(target: ProjectTarget, draft: dict) -> dict[str, Any]:
    expected = _target_metadata(target, draft, require_existing=True)
    if "current_target" not in draft:
        raise PreparationError(
            "post-mutation verification requires current_target from the reread issue"
        )
    current_draft = {"existing_target": draft["current_target"]}
    current = _target_metadata(target, current_draft, require_existing=True)
    if (
        current["team_id"] != expected["team_id"]
        or current["project_id"] != expected["project_id"]
    ):
        raise PreparationError(
            "concurrent Linear target change detected after mutation: expected team ID "
            f"{expected['team_id']!r} and project ID {expected['project_id']!r}, found "
            f"team ID {current['team_id']!r} and project ID {current['project_id']!r}; "
            "do not auto-repair"
        )
    return expected


def prepare_issue(settings: Settings, target: ProjectTarget, draft: Any) -> dict[str, Any]:
    if not isinstance(draft, dict):
        raise PreparationError("draft input must be a JSON object")
    allowed = {
        "title", "context", "outcome", "done_when", "boundaries", "task_kind",
        "primary_category", "research_modifier", "agent", "existing_labels", "workspace_labels",
        "existing_target",
    }
    unknown = set(draft) - allowed
    if unknown:
        raise PreparationError(f"draft contains unknown fields: {sorted(unknown)!r}")
    task_kind, primary_category, research_modifier = _classification(settings, draft)
    target_metadata = _target_metadata(target, draft)

    title = _nonempty_text(draft.get("title"), "title")
    if "\n" in title or "\r" in title:
        raise PreparationError("title must be a single line")
    context = _optional_text(draft.get("context"), "context")
    outcome = _nonempty_text(draft.get("outcome"), "outcome")
    done_when = _string_list(draft.get("done_when"), "done_when", required=True)
    boundaries = _string_list(draft.get("boundaries", []), "boundaries")

    raw_agent = draft.get("agent")
    if not isinstance(raw_agent, dict):
        raise PreparationError("agent must be an object")
    allowed_agent = {"repository_context", "guidance", "constraints", "validation"}
    unknown_agent = set(raw_agent) - allowed_agent
    if unknown_agent:
        raise PreparationError(f"agent contains unknown fields: {sorted(unknown_agent)!r}")
    agent = {
        key: _string_list(raw_agent.get(key, []), f"agent.{key}", required=key == "validation")
        for key in ("repository_context", "guidance", "constraints", "validation")
    }

    existing_labels, workspace_labels = _label_snapshot(draft)
    intended_labels, change_type = _intended_workflow(
        settings, primary_category, research_modifier
    )

    resolved = _workflow_catalog(settings, workspace_labels)
    workflow_names = _workflow_names(settings)
    existing_workflow = [label for label in existing_labels if label.name in workflow_names]
    duplicate_workflow_names = {
        label.name for label in existing_workflow
        if sum(existing.name == label.name for existing in existing_workflow) > 1
    }
    if duplicate_workflow_names:
        raise PreparationError(
            f"issue has ambiguous canonical workflow labels: {sorted(duplicate_workflow_names)!r}"
        )
    for label in existing_workflow:
        catalog_match = resolved[label.name]
        if catalog_match.id != label.id:
            raise PreparationError(f"canonical label {label.name!r} has inconsistent IDs")

    stale = [label for label in existing_workflow if label.name not in intended_labels]
    if stale and not target.replace_workflow_labels:
        raise PreparationError(
            "existing workflow labels conflict with the classification: "
            f"{[label.name for label in stale]!r}; clarify it or explicitly configure replacement"
        )
    removed = stale if target.replace_workflow_labels else []
    added: list[Label] = []
    existing_ids = {label.id for label in existing_labels}
    for name in intended_labels:
        label = resolved[name]
        if label.id not in existing_ids:
            added.append(label)

    description = render_description(
        context=context,
        outcome=outcome,
        done_when=done_when,
        boundaries=boundaries,
        agent=agent,
        task_kind=task_kind,
        primary_category=primary_category,
        research_modifier=research_modifier,
        intended_labels=intended_labels,
        change_type=change_type,
        stop_condition=settings.stop_condition,
    )
    return {
        "target": target_metadata,
        "issue": {"title": title, "description": description},
        "classification": {
            "task_kind": task_kind,
            "primary_category": primary_category,
            "research_modifier": research_modifier,
            "intended_workflow_labels": intended_labels,
            "change_type": change_type,
        },
        "label_changes": {
            "add": [label.id for label in added],
            "remove": [label.id for label in removed],
        },
    }


def verify_workflow_labels(
    settings: Settings, target: ProjectTarget, draft: Any
) -> dict[str, Any]:
    if not isinstance(draft, dict):
        raise PreparationError("verification input must be a JSON object")
    _, primary_category, research_modifier = _classification(settings, draft)
    target_metadata = _verify_target_metadata(target, draft)
    current_labels, workspace_labels = _label_snapshot(draft)
    intended_labels, _ = _intended_workflow(settings, primary_category, research_modifier)
    resolved = _workflow_catalog(settings, workspace_labels)

    workflow_names = _workflow_names(settings)
    actual = [label for label in current_labels if label.name in workflow_names]
    for label in actual:
        catalog_match = resolved[label.name]
        if catalog_match.id != label.id:
            raise PreparationError(f"canonical label {label.name!r} has inconsistent IDs")
    actual_names = [label.name for label in actual]
    if len(actual_names) != len(set(actual_names)) or set(actual_names) != set(intended_labels):
        raise PreparationError(
            "concurrent Linear change detected after mutation: intended workflow labels "
            f"{intended_labels!r}, found {actual_names!r}; do not auto-repair"
        )
    return {
        "verified": True,
        "target": target_metadata,
        "workflow_labels": intended_labels,
    }


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(
        description="Validate and render a configured agent-ready Linear issue"
    )
    result.add_argument("--config", type=Path, default=DEFAULT_CONFIG)
    result.add_argument("--repository", type=Path, required=True)
    result.add_argument("--project-key")
    result.add_argument("--input", type=Path, help="draft JSON path; omit to read standard input")
    result.add_argument(
        "--verify-labels",
        action="store_true",
        help="verify freshly re-read workflow labels after mutation without repairing them",
    )
    return result


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        settings = load_settings(args.config)
        target = resolve_target(settings, args.repository, args.project_key)
        if args.input is None:
            draft = json.load(sys.stdin)
        else:
            with args.input.open(encoding="utf-8") as source:
                draft = json.load(source)
        result = (
            verify_workflow_labels(settings, target, draft)
            if args.verify_labels
            else prepare_issue(settings, target, draft)
        )
        json.dump(result, sys.stdout, indent=2, ensure_ascii=False)
        sys.stdout.write("\n")
        return 0
    except (PreparationError, OSError, UnicodeError, json.JSONDecodeError) as error:
        print(f"Cannot prepare Linear issue: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
