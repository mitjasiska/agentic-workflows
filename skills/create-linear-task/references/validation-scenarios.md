# Forward-test scenarios

Use current repository and Linear configuration. Never substitute fixed team, project, prefix, state, or label IDs, and never create a disposable issue merely to test the skill.

1. **Activation:** Explicitly invoke `$create-linear-task`, then make an equivalent task-authoring request without naming it. Both select this workflow.
2. **Draft only:** Remove Linear access or authorization. The skill returns a complete draft, identifies the missing capability, and makes no write.
3. **Ambiguous target:** Provide no current issue and more than one plausible target. The skill asks one concise question rather than guessing.
4. **Verified creation:** With an authorized real outcome, create one issue in the exact resolved target, then verify content, labels, state, and relationships from a fresh read.
5. **Refinement:** Update an existing issue while preserving its team, project, unrelated labels, and unrelated relationships.
6. **Relationship change:** Apply an explicitly requested parent or related-link change and verify it from a fresh read.
7. **Idempotency:** Repeat a completed preparation. It resolves to the existing issue and makes no write.
8. **Concurrent change:** Change target or workflow labels between the pre-write snapshot and verification. The skill reports the mismatch and does not auto-repair it.
