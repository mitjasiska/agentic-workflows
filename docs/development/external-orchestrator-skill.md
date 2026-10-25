# Skills and plugin development

[`skills/create-linear-task/`](../../skills/create-linear-task/) and [`skills/external-orchestrator/`](../../skills/external-orchestrator/) are the only maintained sources for the repository's skills. Installed skill directories and plugin caches are deployment artifacts, not sources of truth.

`external-orchestrator` helps choose the next action from the current Linear queue, assess implementation readiness, or reconcile architecture changes with open tasks. `create-linear-task` owns issue drafting, classification, targeted issue updates, mutation safety, and fresh post-write verification. The orchestrator delegates issue writes to it and uses its verified result.

These skills provide reusable planning and task procedures. AW owns managed execution; the skills do not start a second control plane or define the user's entire software-development process. Personal choices about execution pipelines and cross-project development belong in the user's own maintained workflow documentation.

## Use the skills

- Ask `external-orchestrator` what to work on next to receive a read-only recommendation grounded in the current project queue and dependencies. An issue need not be selected in advance, and the recommendation does not start execution.
- Ask it to prepare a requested outcome to assess architecture readiness. It reports `Ready` or `Architecture decision required` with evidence and a next action. A missing tool or ambiguous target is reported as a context blocker. Actual readiness assessments carry a compact trace; routine queue lookups and issue-state updates do not.
- Ask it to reconcile an accepted architecture change to identify stale tasks and propose exact updates. Material product decisions still require human acceptance and authoritative documentation before dependent task updates.
- Ask `create-linear-task` to create or refine a task, or to apply a specific content, label, relationship, assignment, or state update. Targeted changes preserve unrelated fields; closing a task checks its actual completion criteria and supporting evidence.

Direct authorization for an exact issue update remains valid throughout that operation. Newly discovered changes require their own authorization. A readiness result alone does not authorize issue mutation, execution, Git publication, or plugin publication.

For AW-managed submission, [ADR 0031](../architecture/adr/0031-derive-publication-metadata-from-reviewed-results.md) defines reviewed commit/PR metadata and [ADR 0024](../architecture/adr/0024-leave-linear-completion-to-github-integration.md) leaves Linear completion to the GitHub integration after merge. Other execution paths must identify their actual review evidence; reusing the visible title/body format does not create an AW-managed review or a managed pass identifier. The Linear skill respects the target repository's completion owner and event rather than treating local validation or PR creation as completion.

## Validate

Run the package validator supplied by the agent environment for both skill directories, then execute their forward-test scenarios. Validate [`plugin.json`](../../plugin.json), [the compatibility manifest](../../.codex-plugin/plugin.json), and [the local marketplace](../../.agents/plugins/marketplace.json) as JSON.

Forward tests cover explicit/implicit activation, next-action selection, readiness decisions, safe blocking, delegation, minimal updates, verification, concurrent changes, and repeat-run idempotency. Use isolated simulations for unavailable-tool and failure cases, and report those separately from live integration checks. Do not create disposable live issues for testing.

Repository-owned automated validation belongs in the established .NET solution once repeated computation warrants deterministic code. Do not create a separate language stack for skill tooling.

## Install

For editable local-agent installation, use the repository through the `skills` installer:

```text
npx skills@latest add mitjasiska/agentic-workflows
```

Select the skills needed by the target harness. The installer copies from the canonical `skills/` tree; update installed copies through the installer rather than editing them directly.

For ChatGPT and Codex plugin installation, the repository root is a portable skills-only plugin. [`plugin.json`](../../plugin.json) is the canonical manifest, while [`.codex-plugin/plugin.json`](../../.codex-plugin/plugin.json) provides compatibility with hosts that still use the Codex layout. Portable discovery uses the root `skills/` directory; the compatibility manifest points to that same directory. OpenAI settings in the root manifest's `extensions.com.openai` replace the compatibility overlay rather than merging with it. See the [official packaging guidance](https://developers.openai.com/plugins/build/plugins).

The repo-scoped [marketplace](../../.agents/plugins/marketplace.json) exposes the root plugin for local testing. Restart the ChatGPT desktop app after adding or refreshing the marketplace, install **Agentic Workflows**, and test in a new chat. Workspace or public publication is a separate administrative action and is not performed by repository validation.

After source changes, installed copies and plugin caches may still contain an older revision. Refresh through the installer or plugin update mechanism and verify the loaded files in a new chat; changing the repository does not update an already-loaded skill. Keep those deployed copies out of source maintenance.

The plugin does not bundle an MCP server. Both skills declare their Linear MCP dependency. `create-linear-task` requires direct, current Linear MCP reads before drafting and stops with a concise notice when the required Linear access is unavailable; it does not construct an offline issue draft from incomplete state. Repository-context gaps still produce a focused request when they prevent an unambiguous task.
