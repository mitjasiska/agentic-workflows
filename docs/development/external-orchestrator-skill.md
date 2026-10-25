# Skills and plugin development

[`skills/create-linear-task/`](../../skills/create-linear-task/) and [`skills/external-orchestrator/`](../../skills/external-orchestrator/) are the only maintained sources for the repository's skills. Installed skill directories and plugin caches are deployment artifacts, not sources of truth.

`external-orchestrator` helps choose the next action from the current Linear queue, assess implementation readiness, reconcile architecture changes with open tasks, or handle explicitly requested external publication. `create-linear-task` owns issue drafting, classification, targeted issue updates, mutation safety, and fresh post-write verification. The orchestrator delegates issue writes to it and uses its verified result.

These skills provide reusable planning and task procedures. AW owns managed execution; the skills do not start a second control plane or define the user's entire software-development process. Personal choices about execution pipelines and cross-project development belong in the user's own maintained workflow documentation.

## Use the skills

- Ask `external-orchestrator` what to work on next to receive a read-only recommendation grounded in the current project queue and dependencies. An issue need not be selected in advance, and the recommendation does not start execution.
- Ask it to prepare a requested outcome to assess architecture readiness. It reports `Ready` or `Architecture decision required` with evidence and a next action. A missing tool or ambiguous target is reported as a context blocker. Actual readiness assessments carry a compact trace; routine queue lookups and issue-state updates do not.
- Ask it to reconcile an accepted architecture change to identify stale tasks and propose exact updates. Material product decisions still require human acceptance and authoritative documentation before dependent task updates.
- Ask `create-linear-task` to create or refine a task, or to apply a specific content, label, relationship, assignment, or state update. Targeted changes preserve unrelated fields; closing a task checks its actual completion criteria and supporting evidence.
- Explicitly ask the Orchestrator to prepare publication metadata, commit, push, or create a PR to select the [external publication procedure](../../skills/external-orchestrator/references/publication.md). Proposals have no Git/forge side effects. Task-linked work uses current Linear labels; permitted taskless work needs no Linear access or issue creation. Direct `main` commits require the user's explicit route choice and repository permission.

Direct authorization for an exact issue update remains valid throughout that operation. Newly discovered changes require their own authorization. A readiness result alone does not authorize issue mutation, execution, Git publication, or plugin publication.

Architecture discussions and task preparation do not trigger publication. Creating a Linear task for `awctl` ends with the verified issue; execution is a separate requested operation. Preparing publication metadata is also separate from committing, pushing, or opening a PR. Permission to use taskless work or direct `main` commits permits a route without authorizing any action through it.

For AW-managed submission, [ADR 0031](../architecture/adr/0031-derive-publication-metadata-from-reviewed-results.md) defines reviewed commit/PR metadata and [ADR 0024](../architecture/adr/0024-leave-linear-completion-to-github-integration.md) leaves Linear completion to the GitHub integration after merge. External architecture and small routine contributions follow the human's chosen review process and repository rules; the Orchestrator adds no independent-review gate. Report the checks and reviews actually performed. Reusing the visible title/body format does not create an AW-managed review or a managed pass identifier. The Linear skill respects the target repository's completion owner and event rather than treating local validation or PR creation as completion.

## Fixed publication conventions

The [shared publication conventions](../../skills/create-linear-task/references/publication-conventions.md) preserve the prototype's fixed mappings: Feature/feat, Bug/fix, Chore/chore, Docs/docs, and Refactor/refactor. Research is an independent modifier. Both skills ship together for now; the Orchestrator reads this companion resource without invoking the Linear authoring workflow. Keep both installed when using external publication.

There is no publication TOML file, example to install, home-directory lookup, project override, or configuration synchronization between Linux and Windows. The mappings are maintained in the skill source and travel with it if the skills are extracted later. A future change to those conventions should be an explicit source edit, followed by the usual installed-skill refresh.

For a tracked contribution, exactly one current label must match a primary label in the fixed mapping; unrelated labels are preserved. Missing or ambiguous classification is reported rather than guessed. For taskless work, the Orchestrator proposes one of the same five types from the diff and shows the message before commit approval, without requiring Linear access.

The subject format stays `<type>: <summary> (<TASK-ID>)`, or `<type>: <summary>` without a task. Task-linked PRs use Summary, Tracking, and Validation; taskless PRs omit Tracking and task-closing references. External commits have no required AW review trailer. Review and branch permissions remain governed by the human's workflow and repository rules.

This procedure is skill instructions only. It changes no `awctl`, service, or active runtime configuration. No publication settings are created and no new ignore rule is needed. Later extraction of either skill remains a separate decision.

## Validate

Run the package validator supplied by the agent environment for both skill directories, then execute their forward-test scenarios. Validate [`plugin.json`](../../plugin.json), [the compatibility manifest](../../.codex-plugin/plugin.json), and [the local marketplace](../../.agents/plugins/marketplace.json) as JSON.

Forward tests cover explicit/implicit activation, next-action selection, readiness decisions, safe blocking, delegation, minimal updates, prototype mappings, taskless publication, authorization boundaries, verification, concurrent changes, and repeat-run idempotency. Use isolated simulations for unavailable-tool and failure cases, and report those separately from live integration checks. Do not create disposable live issues for testing.

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
