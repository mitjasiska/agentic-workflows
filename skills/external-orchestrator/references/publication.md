# Explicit publication outside a managed pass

Use this operation only for a human request to prepare publication metadata, make a local commit, push a commit, or create a PR. Architecture discussion, readiness, task creation, and permission to use a route do not select this operation by themselves. This is an instruction-based companion procedure, not an `awctl` command or service feature.

## Establish the requested action

1. Identify the requested action, exact repository, change, target branch, and whether there is a Linear task. Distinguish a text proposal from a Git/forge mutation. If publication intent or scope is unclear, clarify that point without mutating. Apply existing authorization only to the action and scope it covers.
2. Read the applicable repository instructions, inspect the complete proposed diff and relevant existing commits, and establish the actual branch, base, and remote target. Do not sweep unrelated, private, or pre-existing changes into the proposal.
3. Read the [fixed prototype publication conventions](../../create-linear-task/references/publication-conventions.md). Use their task-linked or taskless type selection without loading external configuration. A taskless routine contribution does not require a Linear connection, a new issue, or a readiness trace. Broader work and architectural decisions still follow the repository's task rules.
4. Perform checks proportionate to the change and the repository's requirements. Review is governed by the human's chosen workflow and repository rules; this skill adds no independent-review gate. Record actual checks, results, limitations, and any review performed. Do not invent a review or an AW-managed pass identifier.

## Prepare the metadata

Derive a concise lower-case action summary from the actual change, not from a possibly stale issue title. It has no existing type prefix, task suffix, control characters, doubled whitespace, or sentence-ending punctuation. The complete subject is at most 72 characters:

- Task-linked commit subject and PR title: `<type>: <action summary> (<TASK-ID>)`.
- Taskless commit subject and PR title: `<type>: <action summary>`.

An external commit has no required `Task-Review` trailer. The AW-managed pass trailer and forced-submission marker describe AW provenance; do not manufacture either for an external contribution. Reusing the visible subject/body conventions does not claim an AW-managed review.

For a task-linked PR, use exactly these sections:

```markdown
## Summary

<What changed and why, based on the actual diff.>

## Tracking

[<TASK-ID>](<canonical Linear URL>)

Fixes <TASK-ID>

## Validation

<Actual checks, results, relevant limitations, and review status.>
```

A taskless PR uses `Summary` and `Validation` and omits `Tracking`, the task suffix, and closing references. A direct commit has no PR body. Keep metadata public-safe: exclude credentials, local private paths, private task instructions, and conversational history. Description and validation content are each at most 2,000 characters. State whether independent review occurred truthfully; its absence is not an external publication blocker unless the selected workflow or repository requires it.

Show the exact proposed message, PR title/body when applicable, repository, and branch before seeking any required approval. Preparing this proposal has no Git, forge, Linear, or execution side effects.

## Perform only the authorized steps

- **Local commit:** Follow the repository's diff-review and commit-approval rules. Verify that the approved change and target still match, stage only that scope, inspect the staged diff, make the authorized commit, and show its resulting identity and subject. Direct commits to `main` require the human's explicit route choice and the repository's permission. Taskless eligibility alone does not select `main`.
- **Push:** Follow the repository's separate push-approval rule after showing the resulting commit. Verify the branch, commit, remote, and upstream state before pushing. A changed remote, unexpected ancestry, or divergence requires reconciliation; preserve work rather than force-pushing or rewriting published history.
- **PR creation:** Require authorization for the PR and verify its exact repository, head/base branches, title, body, and draft/ready choice. Search for an existing PR for that branch before creating another. Prefer the available forge connector when it supports the operation. After creation, reread and verify the PR and attach it to the current chat when the environment provides that capability.

A user request can cover several steps, but it does not waive separate approvals required by repository instructions. If content, conventions, or target changes materially after approval, refresh the affected proposal and authorization. A failed or ambiguous provider response requires a fresh state read before retrying; never create duplicate PRs or repeat an unverified mutation blindly.

Publication does not mark a task Done merely because a commit or PR exists. Respect the repository's completion owner and event. Delegate authorized Linear changes to `create-linear-task`; taskless work has no Linear state to change. If the human instead requests AW-managed execution/submission, return to that supported workflow without treating this external procedure as managed evidence or invoking `awctl` automatically.
