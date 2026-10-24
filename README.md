# Agentic Workflows

Agentic Workflows is a local-first control plane and dashboard for human-directed agentic work. It turns a captured task into implementation, independent review, durable evidence, and a human-approved submission or exported artifact.

The repository and product are both named Agentic Workflows. This repository contains the completed Initial E2E architecture and the first runnable .NET control-plane slice.

## Current status

The Linux control plane runs as a long-lived process, and the local `awctl status` client reports its software and application protocol versions through JSON-RPC 2.0 over a private Unix-domain socket. See the development guide below for the two-terminal flow. Workflow execution, persistence, integrations, and the dashboard are not implemented yet.

The Initial E2E architecture was completed under [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture). Further implementation begins from separate Linear issues with defined outcomes and acceptance boundaries.

## Documentation

Architecture Documentation is authoritative for product decisions, boundaries, and behavior:

- [Architecture](docs/architecture/architecture.md) defines the complete product architecture, major boundaries, and design status.
- [Workflow specification](docs/architecture/workflow-specification.md) defines detailed workflow mechanics and edge cases.
- [Glossary](docs/architecture/GLOSSARY.md) defines canonical domain language.
- [Architecture decision records](docs/architecture/adr/) preserve durable decisions and their rationale.

Development Documentation:

- [Local .NET development](docs/development/local-control-plane.md) covers prerequisites, restore, build, tests, running the control plane and `awctl status`, endpoint discovery, and recovery.
- [Skills and plugin development](docs/development/external-orchestrator-skill.md) explains the canonical skill sources, validation, and installation paths.

Reference Documentation:

- [Local Control Interface contract](docs/reference/local-control-interface.md) specifies the implemented JSON-RPC 2.0 method, application versioning, framing, errors, and bounds.

Supporting material provides context without becoming a separate source of product truth:

- [Architecture refinement checkpoint](docs/planning/architecture-refinement-checkpoint.md) records the current refinement and implementation-readiness position.
- [Research](docs/research/) contains evidence used to inform decisions.

As the product becomes usable, detailed product documentation belongs under `docs/user/`. Create its index section when real content exists rather than as an empty placeholder.

The README and Architecture Documentation must remain clear to humans. Agent-oriented instructions may be terse, but no product decision should exist only in an agent instruction, task, checkpoint, research note, or chat transcript.

## Development

Linear is the canonical work queue. Repository documents constrain and explain the selected work, but they do not authorize unrelated implementation. Read and follow [AGENTS.md](AGENTS.md) before planning or changing this repository.
