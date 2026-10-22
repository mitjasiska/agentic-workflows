# Agentic Workflows

Agentic Workflows is a local-first control plane and dashboard for human-directed agentic work. It turns a captured task into implementation, independent review, durable evidence, and a human-approved submission or exported artifact.

The repository and product are both named Agentic Workflows. This repository contains the completed Initial E2E architecture and will grow into the production implementation in the same codebase.

## Current status

The Initial E2E architecture is ready for implementation-task preparation. Architecture refinement is tracked by [DEV-119](https://linear.app/mitjasiska/issue/DEV-119/complete-initial-e2e-architecture-readiness). Application implementation must begin from a separate Linear issue with a defined outcome and acceptance boundary.

## Documentation

Architecture Documentation is authoritative for product decisions, boundaries, and behavior:

- [Architecture](docs/architecture/architecture.md) defines the complete product architecture, major boundaries, and design status.
- [Workflow specification](docs/architecture/workflow-specification.md) defines detailed workflow mechanics and edge cases.
- [Glossary](docs/architecture/GLOSSARY.md) defines canonical domain language.
- [Architecture decision records](docs/architecture/adr/) preserve durable decisions and their rationale.

Supporting material provides context without becoming a separate source of product truth:

- [Architecture refinement checkpoint](docs/planning/architecture-refinement-checkpoint.md) records the current refinement and implementation-readiness position.
- [Research](docs/research/) contains evidence used to inform decisions.

As the product becomes usable, detailed product documentation belongs under `docs/user/`, development documentation under `docs/development/`, and technical reference material under `docs/reference/`. These sections should be created when they have real content rather than as empty placeholders.

The README and Architecture Documentation must remain clear to humans. Agent-oriented instructions may be terse, but no product decision should exist only in an agent instruction, task, checkpoint, research note, or chat transcript.

## Development

Linear is the canonical work queue. Repository documents constrain and explain the selected work, but they do not authorize unrelated implementation. Read and follow [AGENTS.md](AGENTS.md) before planning or changing this repository.
