# Allow concurrent Workflows for distinct Task scopes

Workflows with different scopes of the same Task may execute concurrently in separate worktrees. This includes a `WholeTask` Workflow and one or more `NamedSlice(name)` Workflows, or several differently named slices. AW does not serialize these Workflows merely because they share a Task.

The existing admission rule still rejects a duplicate Workflow for the same Task and scope under the current identity model. AW does not prove that distinct scopes are semantically non-overlapping. If concurrent Workflows modify overlapping content or one advances the shared base, ordinary Git integration and a fresh independent review handle the resulting work. This preserves development speed without inventing a speculative global scheduler or false scope guarantees.
