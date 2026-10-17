# Keep PR creation manual by default

A clean review stores a durable Review Report for exact uncommitted worktree changes and leaves the workflow ready for human inspection instead of committing automatically. Like the prototype, v1 exposes one explicit `awctl pr` command that composes internal commit, push, and forge-submission actions; optional PR-on-clean automation may invoke the same wrapper later. The internal seams permit separate public commands in a later version if usage justifies them, while v1 preserves a human inspection window and the lowest-friction publication path.
