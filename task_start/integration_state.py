"""One durable integration claim, serialized by the task's publication lock.

It never authorizes replay. Only the separate proven rebase journal authorizes
installation recovery; uncertain agent checkouts are retained for inspection.
Explicit abandonment archives the complete attempt before a new claim may replace it.
"""

from copy import deepcopy
from hashlib import sha256
import json
from datetime import datetime
from pathlib import Path
import re
from uuid import UUID

from . import TaskError
from .ownership import ownership_operation
from .codex_rpc import validate_readiness_only
from .implementation_pass import parse_implementation
from .publication_state import PublicationStore
from .review_result import unique_object
from .sessions import has_immutable_identity
from .workspace import branch_name, slice_slug


def parse_result(raw, record):
    try:
        value = json.loads(raw, object_pairs_hook=unique_object)
        if (set(value) != {"pass_id", "state", "summary", "checks", "source_fingerprint", "base_commit"}
                or value["state"] not in {"completed", "human_decision", "blocked", "failed"}
                or value["source_fingerprint"] != record["source"]["fingerprint"]
                or value["base_commit"] != record["base"]):
            raise ValueError("invalid integration result")
        parse_implementation(json.dumps(dict(pass_id=value["pass_id"],
            state="blocked" if value["state"] == "human_decision" else value["state"],
            summary=value["summary"], checks=value["checks"], resolutions=[])), record["pass_id"], [])
        if value["state"] == "completed" and not any(c["result"] == "passed" for c in value["checks"]):
            raise ValueError("completion requires validation evidence")
        return value
    except (ValueError, TypeError, KeyError, RecursionError):
        raise TaskError("Malformed integration result; inspect the retained checkout and context. No installation is allowed") from None

class IntegrationStore(PublicationStore):
    def __init__(self, worktree=None, *, publication_store=None):
        if publication_store is None:
            super().__init__(worktree)
        else:
            self.directory = publication_store.directory
        self.path = self.directory / "agentic-workflows-integration.json"

    def read(self):
        try:
            if self.path.is_symlink() or self.path.stat().st_size > 1024 * 1024:
                raise ValueError("invalid record")
            value = json.loads(self.path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
            if (value["version"] != 1 or str(UUID(value["pass_id"])) != value["pass_id"]
                    or value["state"] not in {"preparing", "running", "stopped", "uncertain", "proven", "installed", "abandoned"}
                    or not Path(value["checkout"]).is_absolute()
                    or not isinstance(value["binding"], dict)
                    or not isinstance(value["source"], dict)
                    or not isinstance(value["base"], str)
                    or not isinstance(value["options"], dict)
                    or (value["plan"] is not None and not isinstance(value["plan"], dict))):
                raise ValueError("invalid record")
            if (set(value["binding"]) != {"issue", "repository", "worktree", "branch", "base_branch", "endpoint", "workspace_id"}
                    or any(not isinstance(v, str) or not v for v in value["binding"].values())
                    or any(not Path(value["binding"][k]).is_absolute() for k in ("repository", "worktree", "endpoint"))
                    or set(value["options"]) != {"kind", "model", "mode"}
                    or any(not isinstance(v, str) or not v for v in value["options"].values())
                    or not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", value["base"])):
                raise ValueError("invalid identity")
            # Earlier claims remain inspectable, but are never replayed.
            # New executions retain their output location and scope.
            if "slice" in value and value["slice"] is not None:
                scope = value["slice"]
                if (not isinstance(scope, str) or slice_slug(scope) != scope
                        or branch_name(value["binding"]["issue"], scope) != value["binding"]["branch"]):
                    raise ValueError("invalid integration slice")
            if "output" in value:
                output = Path(value["output"])
                if (not output.is_absolute() or output.name != "result.json" or ".." in output.parts
                        or any(output.is_relative_to(Path(p)) for p in (value["checkout"],
                            value["binding"]["repository"], value["binding"]["worktree"]))):
                    raise ValueError("invalid integration output path")
            if "isolated_index" in value and (set(value["isolated_index"]) != {"entries", "flags"}
                    or any(not isinstance(v, str) or not re.fullmatch(r"[0-9a-f]{64}", v)
                           for v in value["isolated_index"].values())):
                raise ValueError("invalid isolated index identity")
            if value["state"] in {"proven", "installed"}:
                from .publication_rebase import validate_record
                plan = value["plan"]
                if not isinstance(plan, dict):
                    raise ValueError("missing proof")
                validate_record(plan)
                if (plan["binding"] != value["binding"] or plan["source"] != value["source"]
                        or plan["base"] != value["base"] or plan["identity"] != value["identity"]
                        or plan.get("integration_id") != value["pass_id"]
                        or value["completion"]["state"] != "completed"
                        or value["state"] == "installed" and plan["result"] is None):
                    raise ValueError("invalid integration proof")
                parse_result(json.dumps(value["completion"]), value)
                context = value["context"]
                if (set(context) != {"context_id", "agent", "model", "mode", "session"}
                        or not re.fullmatch(re.escape(value["binding"]["issue"]) + r"-G[1-9][0-9]*", context["context_id"])
                        or not has_immutable_identity(context["session"], context["agent"])
                        or any(context[k] != value["options"][o] for k, o in
                               (("agent", "kind"), ("model", "model"), ("mode", "mode")))):
                    raise ValueError("invalid execution proof")
                if value["state"] == "installed" and (
                        len(plan["commits"]) != len(plan["steps"]) or plan["result"]["head"] != plan["commits"][-1]):
                    raise ValueError("invalid installed proof")
            if value["state"] == "abandoned":
                abandoned_context(value)
            return value
        except FileNotFoundError:
            return None
        except (OSError, ValueError, TypeError, KeyError, AttributeError, UnicodeError, RecursionError):
            raise TaskError("Cannot read integration provenance; inspect private Git metadata and retained checkout. No replay is safe") from None

    @ownership_operation
    def write(self, value, *, before_replace=None):
        from .cleanup import check_unreserved
        from .contexts import ContextRegistry
        binding = value.get("binding", {})
        registry = ContextRegistry()
        for path in (binding.get("worktree"), value.get("checkout"), value.get("output"), str(self.directory)):
            check_unreserved(registry, binding.get("repository"), path)
        if len(json.dumps(value, ensure_ascii=True).encode()) > 1024 * 1024:
            raise TaskError("Integration evidence exceeds its bounded record; inspect the retained checkout manually")
        if before_replace is None:
            super().write(value)
        else:
            super().write(value, before_replace=before_replace)

    def archive(self, record, *, create=False):
        context = abandoned_context(record)
        archive = IntegrationStore(publication_store=self)
        archive.path = self.directory / f"agentic-workflows-integration-{context['context_id']}.json"
        prior = archive.read()
        if prior is None and create:
            archive.write(record)  # Same fsync/atomic replace, under the task lock.
        elif prior != record:
            raise TaskError("Abandoned integration archive is missing or changed; inspect it before recovery")

    def check_abandoned(self, record, registry, issue):
        """All previous G ordinals need explicit, durable, completed abandonment."""
        if IntegrationStore(Path(record["binding"]["worktree"])).directory != self.directory:
            raise TaskError("Abandoned integration belongs to a different task checkout; no retry is safe")
        contexts = [c for c in registry.list(issue, include_retired=True)
                    if c["role"] == "integration" and c["state"] != "retired"]
        if abandoned_context(record) not in contexts:
            raise TaskError("Integration abandonment is incomplete; repeat the explicit --abandon action after inspection")
        self.archive(record)
        for context in contexts:
            if not re.fullmatch(re.escape(issue) + r"-G[1-9][0-9]*", context["context_id"]):
                raise TaskError("Integration context identity is invalid; no retry is safe")
            archive = IntegrationStore(publication_store=self)
            archive.path = self.directory / f"agentic-workflows-integration-{context['context_id']}.json"
            prior = archive.read()
            if (prior is None or prior["state"] != "abandoned" or abandoned_context(prior) != context
                    or prior["binding"] != record["binding"]):
                raise TaskError("Integration history lacks matching abandonment provenance; inspect it manually")

    def disposal_records(self):
        """Read current and archived attempts before their Git directory disappears."""
        records = {}
        paths = [self.path, *sorted(self.directory.glob("agentic-workflows-integration-*.json"))]
        if len(paths) > 101:
            raise TaskError("Too many retained integrations for bounded forced cleanup; inspect history manually")
        for path in paths:
            source = IntegrationStore(publication_store=self)
            source.path = path
            record = source.read()
            if record is None:
                if path != self.path:
                    raise TaskError("Integration archive disappeared during cleanup inspection")
                continue
            if path != self.path and (record["state"] != "abandoned"
                    or path.name != f"agentic-workflows-integration-{abandoned_context(record)['context_id']}.json"):
                raise TaskError("Integration archive identity changed; disposal refused")
            previous = records.setdefault(record["pass_id"], record)
            if previous != record:
                raise TaskError("Conflicting retained integration evidence; disposal refused")
        return list(records.values())


def evidence_digest(value):
    return sha256(json.dumps(value, sort_keys=True, ensure_ascii=True).encode()).hexdigest()


def integration_tombstone(record):
    """Bounded provenance, without retaining reports, conflicted files or handles."""
    return dict(pass_id=record["pass_id"], state=record["state"], binding=record["binding"],
                checkout=record["checkout"], output=record.get("output"), base=record["base"],
                context_id=(record.get("context") or {}).get("context_id"),
                source_fingerprint=record["source"].get("fingerprint"),
                source_head=record["source"].get("head"), options=record["options"],
                slice=record.get("slice"), slice_recorded="slice" in record,
                record_sha256=evidence_digest(record), plan_sha256=evidence_digest(record["plan"]),
                completion_sha256=evidence_digest(record.get("completion")),
                abandonment_sha256=evidence_digest(record.get("abandonment")))


def abandoned_context(record):
    """Validate the explicit disposition and reconstruct the retained registry row."""
    try:
        decision, target = record["abandonment"], record["binding"]
        context, proof = decision["context"], decision["proof"]
        timestamp = datetime.fromisoformat(decision["at"])
        if (record["state"] != "abandoned" or record["plan"] is not None or "completion" in record
                or timestamp.tzinfo is None
                or context["state"] != "uncertain" or context["retired_at"] is not None
                or context["role"] != "integration" or context["agent"] != "codex"
                or context["resumability"] != "unknown"
                or any(context[k] is not None for k in ("session_id", "session_kind", "herdr_session"))
                or not re.fullmatch(re.escape(target["issue"]) + r"-G[1-9][0-9]*", context["context_id"])
                or record["context"] != {"context_id": context["context_id"]}
                or context["worktree"] != record["checkout"]
                or any(context[k] != target[k] for k in ("issue", "repository", "endpoint", "workspace_id"))
                or any(context[k] != record["options"][v] for k, v in (("agent", "kind"), ("model", "model"), ("mode", "mode")))
                or proof["provider"] != "codex" or proof["checkout"] != record["checkout"]
                or proof["history"] not in {"absent", "readiness_only"}
                or set(proof["process"]) != {"pid", "started"}
                or any(v is not None and (type(v) is not int or v <= 0) for v in proof["process"].values())
                or (proof["process"]["pid"] is None) != (proof["process"]["started"] is None)):
            raise ValueError("invalid abandonment")
        if proof["history"] == "readiness_only":
            if any(k in record for k in ("output", "slice", "isolated_index")) or proof["process"]["pid"] is None:
                raise ValueError("readiness recovery requires a legacy claim")
            validate_readiness_only(proof["startup"], Path(record["checkout"]))
        elif "startup" in proof:
            raise ValueError("unexpected startup history")
        return dict(context, state="abandoned", retired_at=decision["at"])
    except (KeyError, ValueError, TypeError, AttributeError):
        raise TaskError("Invalid integration abandonment provenance; no retry is safe") from None


class IntegrationInstallation:
    """One live guard for initial/recovered mutations and authoritative results.

    Freeze the proven record: the installer's commits/result evolve in memory,
    but must never change the binding, scope, proven source or base SHA that
    authorizes installation. Moving local/remote base refs cannot change this
    proof. Publication eligibility is checked separately by task pr.
    """

    def __init__(self, store, saved, record, registry, issue, project, repo, identities):
        if "slice" not in record:
            raise TaskError("Integration proof lacks recorded slice scope; inspect it before continuing installation")
        self.store, self.saved = store, saved
        self.integration = IntegrationStore(publication_store=store)
        self.record = deepcopy(record)
        self.registry, self.issue, self.project = registry, issue, project
        self.repo, self.identities = repo, identities
        self.progress = 0
        self()

    def __call__(self):
        from .review import resolve_review_workspace
        from .publication_rebase import installation_state
        from .publish import verify_remote_identity
        from .review_state import snapshot
        from .workspace import Git
        workspace, _, _, endpoint = resolve_review_workspace(
            self.issue, self.project, self.repo, self.registry, self.identities, local_only=True)
        target = dict(issue=self.issue.identifier, repository=str(self.repo), worktree=str(workspace.path),
                      branch=workspace.branch, base_branch=self.project.base_branch, endpoint=endpoint,
                      workspace_id=workspace.workspace_id)
        if (target != self.record["binding"]
                or PublicationStore(workspace.path).directory != self.store.directory):
            raise TaskError("Integration task identity changed; inspect the pending evidence")
        rebase = self.saved["rebase"]
        # The source snapshot and base are immutable plan identity. Only commit
        # construction and the eventual result may progress during installation.
        if (self.integration.read() != self.record or self.store.read() != self.saved
                or rebase is None or any(rebase.get(k) != v for k, v in self.record["plan"].items()
                                         if k not in {"commits", "result"})):
            raise TaskError("Integration provenance changed during installation; inspect the pending evidence")
        # These are local configuration/identity checks, never remote ref reads.
        git = Git(workspace.path)
        for checkout in (git, Git(self.repo)):
            verify_remote_identity(checkout, self.project.base_branch, workspace.branch, self.record["identity"])
        current, at_target = installation_state(git, rebase)
        progress = (3 if at_target and current.head == rebase["commits"][-1] else
                    2 if at_target else 1 if current.content == rebase["content"] else 0)
        if progress < self.progress or (rebase["result"] is not None and
                snapshot(workspace.path, rebase["base"], workspace.branch).as_dict() != rebase["result"]):
            raise TaskError("Integration installation state changed; inspect the pending evidence")
        self.progress = progress
        # Re-read scope after the live workspace/identity observations, immediately
        # before handing control back to the guarded mutation.
        scope = Git(self.repo).resolve_scope(workspace.path, workspace.branch, self.issue.identifier, None)
        if scope != self.record["slice"] or workspace.slice != self.record["slice"]:
            raise TaskError("Integration slice scope changed; inspect its provenance before continuing installation")

    def finish(self):
        from .publication_rebase import validate_commits, validate_record
        from .workspace import Git
        rebase = self.saved["rebase"]
        validate_record(rebase)
        path = Path(self.record["binding"]["worktree"])
        if (rebase["result"] is None
                or validate_commits(Git(path), rebase) != rebase["result"]["head"]):
            raise TaskError("Integration installed history differs from provenance; inspect it manually")
        installed = dict(self.record, state="installed", plan=deepcopy(rebase))
        self.integration.write(installed, before_replace=self)


def check_integration(store, saved, registry, issue, project, repo, identities):
    """Gate review/publication; return a live identity guard for pending installs."""
    integration = IntegrationStore(publication_store=store)
    record = integration.read()
    contexts = [c for c in registry.list(issue.identifier, include_retired=True)
                if c["role"] == "integration" and c["state"] != "retired"]
    if record is None:
        if contexts or (saved["rebase"] is not None and saved["rebase"].get("integration_id")):
            raise TaskError("Integration controller state is missing; inspect the isolated context. No replay or installation is inferred")
        return
    if record["binding"].get("issue") != issue.identifier:
        raise TaskError("Integration issue identity changed; inspect its provenance")
    if record["state"] == "installed":
        return
    if record["state"] == "abandoned" and saved["rebase"] is None:
        integration.check_abandoned(record, registry, issue.identifier)
        return
    plan, rebase = record["plan"], saved["rebase"]
    if (record["state"] == "proven" and plan is not None and rebase is not None
            and all(rebase.get(k) == v for k, v in plan.items() if k not in {"commits", "result"})):
        guard = IntegrationInstallation(store, saved, record, registry, issue, project, repo, identities)
        # Publication's existing validator/installer owns continuation. When its
        # durable result exists, future intentional edits may start a new review.
        if rebase.get("result") is not None:
            guard.finish()
            return
        return guard
    raise TaskError(f"Integration is {record['state']}; inspect its context and retained checkout at {record['checkout']}. "
                    "No automatic prompt replay or partial installation is allowed")
