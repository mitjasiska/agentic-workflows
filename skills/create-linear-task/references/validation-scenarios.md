# Forward-test scenarios

Use current repository and Linear configuration. Never substitute fixed team, project, prefix, state, or label IDs, and never create a disposable issue merely to test the skill.

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
