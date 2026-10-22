# Forward-test scenarios

Run these scenarios when changing the workflow. Use current repository and Linear configuration; never substitute fixed team, project, prefix, state, or label values.

1. **Activation:** Explicitly invoke `$external-orchestrator`, then make an equivalent task-preparation request without naming it. Both select task preparation. An explicitly assigned managed Implementer or Reviewer pass does not.
2. **Ready:** Evaluate an authorized task with a complete outcome and acceptance boundary. The trace validates as `Ready`, including any deliberate Implementation Latitude.
3. **Blocked:** Test both a Product Invariant conflict and a material Open Area. Each returns `Architecture decision required`, identifies one focused decision, and makes no Linear write.
4. **Verified creation:** With authorization, prepare a real Ready outcome that has no matching issue. Delegate creation to `create-linear-task`, then reread the issue through Linear MCP and verify target, content, labels, state, and relationships.
5. **Idempotency:** Repeat the same preparation. It resolves to the verified existing issue and creates no duplicate.
6. **Reconciliation:** Change an authoritative statement in a controlled test or real authorized change. Identify stale open tasks and exact proposed updates, make no task write before approval, apply only approved refinements through `create-linear-task`, verify them through Linear MCP, and rerun readiness.

Do not create disposable Linear issues merely to test the skill. Use an authorized real task, or stop after the no-write scenarios when no such task exists.
