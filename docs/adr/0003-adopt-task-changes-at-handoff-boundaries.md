# Adopt task changes only at handoff boundaries

Each agent pass is bound to one immutable Task Snapshot, but a Workflow may contain a sequence of snapshots when the user explicitly adopts changed source requirements. AW never interrupts an active pass: before the next handoff it detects a new source revision, pauses once for that revision, and lets the user retain the active snapshot, adopt a new snapshot, or stop; adopting a new snapshot always routes work back to implementation before review.
