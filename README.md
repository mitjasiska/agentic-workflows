# Agentic Workflows

Agentic Workflows is a local-first control plane and dashboard for human-directed agentic work. It turns a captured task into implementation, independent review, durable evidence, and a human-approved submission or exported artifact.

The repository and product are both named Agentic Workflows. This repository contains the completed Initial E2E architecture and the Linux .NET control plane.

## Current status

The Linux control plane exposes `awctl` commands through JSON-RPC 2.0 over a private Unix-domain socket. The managed Linear workflow slice persists requirements and evidence in SQLite, prepares local Herdr workspaces, and routes Codex implementation through independent review. It stops before commit, push, and pull-request creation. The dashboard and submission slice remain separate work. Deterministic controlled validation is the blocking gate for this slice. Live Linear/Herdr/Codex integration remains deferred and unverified; validation belongs to the future opt-in E2E kit.

The Initial E2E architecture was completed under [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture). Planned implementation begins from separate Linear issues with defined outcomes and acceptance boundaries. Small routine changes explicitly requested by the user may proceed without an issue under the contribution exception in [AGENTS.md](AGENTS.md).

## Documentation

Architecture Documentation is authoritative for product decisions, boundaries, and behavior:

- [Architecture](docs/architecture/architecture.md) defines the complete product architecture, major boundaries, and design status.
- [Workflow specification](docs/architecture/workflow-specification.md) defines detailed workflow mechanics and edge cases.
- [Glossary](docs/architecture/GLOSSARY.md) defines canonical domain language.
- [Architecture decision records](docs/architecture/adr/) preserve durable decisions and their rationale.

Development Documentation:

- [Local .NET development](docs/development/local-control-plane.md) covers prerequisites, build/rebuild/tests, the edit-build-restart loop, manual debugging, and isolated validation.
- [Skills and plugin development](docs/development/external-orchestrator-skill.md) explains the canonical skill sources, explicit external publication, validation, and installation paths.
- [Managed workflow validation and smoke test](docs/development/managed-workflow-smoke-test.md) covers blocking controlled validation and the deferred live integration procedure for the future opt-in E2E kit.

Product Documentation:

- [Linux source installation and service operation](docs/user/source-installation.md) covers fresh-checkout setup, PATH, first start, automatic startup, logs, lifecycle commands, safe removal, and recovery.
- [Managed Linear workflows](docs/user/managed-workflows.md) covers setup, configuration, inspection, continuation, interruption, abandonment, cleanup, and database recovery.

Reference Documentation:

- [Local Control Interface contract](docs/reference/local-control-interface.md) specifies the implemented JSON-RPC methods, application versioning, framing, errors, and bounds.
- [Result Envelope contract](docs/reference/result-envelope.md) specifies correlated role outcomes and immutable evidence.
- [Example Project Configuration](examples/project.toml) documents ordinary settings and credential references.

Supporting material provides context without becoming a separate source of product truth:

- [Architecture refinement checkpoint](docs/planning/architecture-refinement-checkpoint.md) records the current refinement and implementation-readiness position.
- [Research](docs/research/) contains evidence used to inform decisions.

The README and Architecture Documentation must remain clear to humans. Agent-oriented instructions may be terse, but no product decision should exist only in an agent instruction, task, checkpoint, research note, or chat transcript.

## Development

Linear is the canonical work queue for planned work. Repository documents constrain and explain the selected work, but they do not authorize unrelated implementation. Read and follow [AGENTS.md](AGENTS.md) for the routine-change exception and Git approval requirements before planning or changing this repository.
