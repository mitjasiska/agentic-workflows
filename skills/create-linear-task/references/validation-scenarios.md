# Forward-test scenarios

Use current repository and Linear configuration. Never substitute fixed team, project, prefix, state, or label IDs, and never create a disposable issue merely to test the skill.

Use authorized real operations for live mutation checks. Failure, concurrency, and unavailable-tool scenarios may instead use explicitly isolated tool-response simulations; report them as simulated behavior, not live integration evidence. Evaluate actions and payloads rather than matching headings or wording alone.

1. **Activation:** Explicitly invoke `$create-linear-task`, then make an equivalent task-authoring request without naming it. Both select this workflow.
2. **MCP unavailable:** Remove Linear MCP access while leaving another possible Linear interface available. The skill sends a concise unavailability notice, returns no issue draft, and makes no fallback attempt or write.
3. **Incomplete read:** Make one required Linear MCP read fail or return insufficient target data. The skill stops before drafting or mutation and identifies the missing current state.
4. **Proposal with read access:** With complete Linear MCP reads, no write tool, and no authorization to mutate, the skill presents a concise proposal and makes no write.
5. **Ambiguous target:** Provide no current issue and more than one plausible target. The skill asks one concise question rather than guessing.
6. **Concrete human description:** Given a broad goal, the proposed issue opens with `What to do` and says in plain language what work will be done and what result it will produce. The acceptance checks and agent instructions carry the detail without replacing that short explanation.
7. **Verified creation:** With an authorized real outcome, create one issue in the exact resolved target, then verify content, labels, state, and relationships from a fresh read.
8. **Refinement:** Update an existing issue while preserving its team, project, unrelated labels, and unrelated relationships.
9. **Relationship change:** Apply an explicitly requested parent or related-link change and verify it from a fresh read.
10. **Idempotency:** Repeat a completed preparation. It resolves to the existing issue and makes no write.
11. **Concurrent change:** Change target or workflow labels between the pre-write snapshot and verification. The skill reports the mismatch and does not auto-repair it.
12. **Verification unavailable:** Make the fresh read fail after an authorized mutation. The skill reports the attempted but unverified write and does not claim success or retry the mutation.
13. **State-only update:** Request an exact transition on a known issue. Read that issue and its team's states; send only identifier and resolved state, preserve content/classification/relationships, verify the result, and make no write on repetition. Unavailable label tools do not block this operation.
14. **Existing authorization:** Request an exact, already-approved description patch. Apply only that patch without a second approval request or unrelated task rewrite.
15. **Completion evidence:** Request closure when a required validation, review, or delivery condition is unmet. Identify that condition and leave the issue open. Distinguish the repository's actual completion boundary from a presumed merge requirement.
16. **Concurrent pre-write change:** Change the requested field between baseline and pre-write refresh. Report a material conflict before writing. Preserve independent changes to unrelated fields.
17. **Collection preservation:** Add one label or relationship while unrelated entries exist. Use additive fields or merge with the fresh complete collection; verify that unrelated entries survive.
18. **Integration-owned completion:** The repository delegates completion to a forge integration after merge, and the user asks to finish the task. With reviewed local changes or an unmerged PR, prepare the required issue linkage and preserve the open state rather than marking Done early.
