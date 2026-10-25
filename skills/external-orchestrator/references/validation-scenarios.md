# Forward-test scenarios

Run these scenarios when changing the workflow. Use current repository and Linear configuration; never substitute fixed team, project, prefix, state, or label values.

Use authorized real operations for live mutation checks. Explicitly isolated tool-response simulations may cover failures and concurrency without touching live issues; label that evidence as simulated. Evaluate decisions, actions, and mutation payloads rather than exact response wording.

1. **Activation:** Explicitly invoke `$external-orchestrator`, then make an equivalent task-preparation request without naming it. Both select task preparation. An explicitly assigned managed Implementer or Reviewer pass does not.
2. **Ready:** Evaluate an authorized task with a complete outcome and acceptance boundary. The trace validates as `Ready`, including any deliberate Implementation Latitude.
3. **Blocked:** Test both a Product Invariant conflict and a material Open Area. Each returns `Architecture decision required`, identifies one focused decision, and makes no Linear write.
4. **Verified creation:** With authorization, prepare a real Ready outcome that has no matching issue. Delegate creation and fresh verification to `create-linear-task`, then report its verified target, content, labels, state, and relationships.
5. **Idempotency:** Repeat the same preparation. It resolves to the verified existing issue and creates no duplicate.
6. **Reconciliation:** Change an authoritative statement in a controlled test or real authorized change. Identify stale open tasks and exact proposed updates, honor existing authorization for exact changes and request approval for additional ones, delegate only authorized refinements and verification, and rerun readiness.
7. **Next work:** Ask what to work on next without supplying an issue. Read the resolved project's current queue and dependencies, recommend one action, and make no task or execution mutation. A queue recommendation alone does not require a readiness trace.
8. **New outcome:** Explicitly request preparation of an outcome with no issue yet. Resolve the current project and search for matching work; do not require a pre-existing issue merely to begin readiness assessment.
9. **Context unavailable:** Make a required Linear read fail or leave the project target ambiguous. Report the missing context without inventing current state or calling a tooling failure an architecture decision.
10. **Metadata update:** Request an exact issue-state change with no requirements change. Delegate to `create-linear-task` without a readiness assessment or duplicate verification.
11. **Report freshness:** Supply an old clean-review report and incomplete current repository evidence. Distinguish the reported review from current delivery or completion, and recommend the missing check without inferring success.
12. **Portable guidance:** Use a repository that has settled constraints but no named guidance categories or optional architecture-refinement skills installed. Apply the documented meanings, preserve its authority, and present a focused decision without demanding new repository conventions or tools.

Do not create disposable Linear issues merely to test the skill. Use an authorized real task, or stop after the no-write scenarios when no such task exists.
