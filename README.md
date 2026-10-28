# Agentic Workflows

> **Deprecated — active development paused on October 28, 2026.**
> This repository is preserved as an engineering reference. New agent-assisted
> development uses [T3 Code](https://github.com/pingdotgg/t3code), Codex, and reusable
> skills. The strategic pause is reversible; ongoing maintenance is not promised.

Agentic Workflows is a local-first control plane and dashboard for human-directed agentic work. It turns a captured task into implementation, independent review, durable evidence, and a human-approved submission or exported artifact.

This C#/.NET project is the second generation of [Agentic Workflows Prototype](https://github.com/mitjasiska/agentic-workflows-prototype), the experimental Python implementation. Both projects are now deprecated. This repository contains the completed Initial E2E architecture and the Linux .NET control plane; the dashboard remains planned work.

## Engineering highlights

- Durable SQLite-backed workflow state with explicit operation and recovery semantics.
- A local `awctl` CLI using JSON-RPC 2.0 over a private Unix-domain socket.
- Immutable, versioned Linear task snapshots and strictly validated, correlated Result Envelopes.
- Separate implementation and independent review passes, with review authority tied to exact Git content.
- Resumable commit, push, and GitHub PR submission with explicit human authorization.
- Automated validation using isolated Git repositories, SQLite, and controlled providers.

## Why development is paused

I built two generations of Agentic Workflows to explore reliable agent-assisted software delivery. In controlled experiments on October 27, 2026, T3 Code V2 coordinated implementation, independent review, defect repair, and fresh re-review without manual intervention in the tested scenario. Its agent coordination, persistent conversations, and remote/mobile interface addressed much of the experience I had intended to build.

For my current development needs, the additional guarantees of a separate orchestration platform did not justify its implementation and maintenance cost. I chose to focus on building useful applications with T3 Code, Codex, reusable skills, and lightweight integrations.

AW's deterministic lifecycle, immutable requirements, validated evidence, exact-content review, and recovery policies remain valuable engineering work. The small T3 experiments did not establish equivalent guarantees or reliability on complex tasks. This is a decision about priorities and opportunity cost, not a finding that AW's architecture is unsound. Development may resume if real usage reveals recurring limitations that justify a focused solution.

## Implementation and validation

The Linux control plane exposes `awctl` commands through JSON-RPC 2.0 over a private Unix-domain socket. Managed Linear workflows persist requirements and evidence in SQLite, prepare local Herdr workspaces, and route Codex implementation through independent review. Explicit `awctl pr [WORKFLOW-ID]` freezes the authorized change, establishes one commit, safely pushes the task branch, and creates its first non-draft GitHub pull request. Repeating the command verifies and resumes that same submission. Clean review alone never publishes. The dashboard and later PR updates remain separate work. Automated validation uses isolated Git repositories, SQLite, and controlled providers; live Linear/Herdr/Codex/GitHub smoke testing remains unverified and does not yet establish Initial E2E milestone acceptance.

The Initial E2E architecture was completed under [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture). The code, tests, architecture decisions, and documentation below are retained for study and possible future reuse.

## Documentation

Architecture Documentation is authoritative for product decisions, boundaries, and behavior:

- [Architecture](docs/architecture/architecture.md) defines the complete product architecture, major boundaries, and design status.
- [E2E test-kit architecture](docs/architecture/e2e-test-kit.md) records the live test kit's accepted design, remaining execution questions, and relevant decisions from the shared ADR series.
- [Workflow specification](docs/architecture/workflow-specification.md) defines detailed workflow mechanics and edge cases.
- [Glossary](docs/architecture/GLOSSARY.md) defines canonical domain language.
- [Architecture decision records](docs/architecture/adr/) preserve durable decisions and their rationale.

Development Documentation:

- [Local .NET development](docs/development/local-control-plane.md) covers prerequisites, build/rebuild/tests, the edit-build-restart loop, manual debugging, and isolated validation.
- [Skills and plugin development](docs/development/external-orchestrator-skill.md) explains the canonical skill sources, explicit external publication, validation, and installation paths.
- [Managed workflow validation and deferred smoke-test notes](docs/development/managed-workflow-smoke-test.md) covers isolated review-to-PR validation and unverified live integration notes; the complete acceptance procedure and future test kit remain separate work.

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
