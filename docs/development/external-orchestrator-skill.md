# Skills and plugin development

[`skills/create-linear-task/`](../../skills/create-linear-task/) and [`skills/external-orchestrator/`](../../skills/external-orchestrator/) are the only maintained sources for the repository's skills. Installed skill directories and plugin caches are deployment artifacts, not sources of truth.

`create-linear-task` owns Linear issue drafting, classification, relationships, mutation safety, and verification. `external-orchestrator` decides architecture readiness and reconciliation, then delegates every approved Linear issue write to `create-linear-task`.

## Validate

Run the package validator supplied by the agent environment for both skill directories, then execute their forward-test scenarios. Validate [`plugin.json`](../../plugin.json), [the compatibility manifest](../../.codex-plugin/plugin.json), and [the local marketplace](../../.agents/plugins/marketplace.json) as JSON.

Repository-owned automated validation belongs in the established .NET solution once repeated computation warrants deterministic code. Do not create a separate language stack for skill tooling.

## Install

For editable local-agent installation, use the repository through the `skills` installer:

```text
npx skills@latest add mitjasiska/agentic-workflows
```

Select the skills needed by the target harness. The installer copies from the canonical `skills/` tree; update installed copies through the installer rather than editing them directly.

For ChatGPT and Codex plugin installation, the repository root is a portable skills-only plugin. [`plugin.json`](../../plugin.json) is the canonical manifest, while [`.codex-plugin/plugin.json`](../../.codex-plugin/plugin.json) provides compatibility with hosts that still use the Codex layout. Both manifests expose the same root `skills/` directory, so packaging creates no second skill tree.

The repo-scoped [marketplace](../../.agents/plugins/marketplace.json) exposes the root plugin for local testing. Restart the ChatGPT desktop app after adding or refreshing the marketplace, install **Agentic Workflows**, and test in a new chat. Workspace or public publication is a separate administrative action and is not performed by repository validation.

The plugin does not bundle an MCP server. Both skills declare their Linear tool dependency and stop with a draft or a focused configuration request when the host cannot provide Linear or repository context.
