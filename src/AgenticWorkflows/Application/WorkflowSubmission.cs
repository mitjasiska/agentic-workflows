using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Application;

public sealed partial class WorkflowApplication
{
    private void AcceptSubmission(WorkflowData data, bool force)
    {
        var current = Current(data);
        if (current.Kind == "Submission" && current.Status is OperationStatus.Pending or OperationStatus.Running && running.ContainsKey(data.State.Id))
            return; // An already accepted invocation retains its own authorization and Operation.
        if (data.Operations.Any(o => o.PassId is not null && o.Status is OperationStatus.Pending or OperationStatus.Running))
            throw Error("active_pass", "Submission requires every Managed Agent Pass to be confirmed finished; inspect or interrupt the active execution first.");
        var next = NewOperation("Submission", current.Id) with { Force = force };
        WorkflowOperation[] operations = current.Kind == "Submission" && current.Status is OperationStatus.Pending or OperationStatus.Running
            ? [next, current with { Status = OperationStatus.Interrupted, FinishedAt = Now }] : [next];
        Save(data.State with { CurrentOperationId = next.Id, Recovery = null }, "SubmissionRequested",
            force ? "Explicit awctl pr --force invocation accepted; no review bypass is inherited by later invocations." : "Explicit awctl pr invocation accepted.", operations);
    }

    private async Task Submit(WorkflowData data, WorkflowOperation operation, CancellationToken token)
    {
        var state = data.State;
        if (state.Phase == WorkflowPhase.WorkspacePreparation || state.Workspace is null || !state.StartupLinearCompleted)
            throw Error("workspace_incomplete", "Submission requires completed WorkspacePreparation and a usable retained workspace.");
        if (state.Abandoned) throw Error("workflow_abandoned", "Resume the retained Workflow before submission.");
        // Observe retained owned sessions as well as Operation status: an uncertain execution is not safe.
        foreach (var session in data.Sessions)
        {
            var observed = await herdr.ObserveAsync(state.Workspace, session, token);
            if (!observed.Reachable || observed.State is ExecutionState.Running or ExecutionState.Unknown)
                throw Error("active_pass", "A retained owned session is active or uncertain. Confirm it has finished before awctl pr; force cannot bypass execution safety.");
        }
        await ValidateBinding(state, token);
        await CheckSource(data, token);
        data = Get(state.Id); state = data.State;
        // A budget gates another agent pass, not an explicit deterministic submission.
        if (state.Attention.Any(a => a.Reason != AttentionReason.PassBudgetContinuation))
            throw Error("attention_required", "Applicable Attention Requirements must be resolved through the displayed ordinary commands before submission.");
        var checkpoint = await repository.CaptureAsync(state.Workspace!, state.WorkspaceIntent.BaseCommit, token);
        if (state.Submission is { Intent: var retained })
        {
            if (retained.Target != checkpoint.Target || retained.WorktreeIdentity != checkpoint.WorktreeIdentity ||
                retained.SnapshotId != state.ActiveSnapshotId || retained.Binding != state.Binding)
                throw Error("frozen_intent_drift", "Current content or identity differs from the frozen PR Intent. Preserve later work separately and restore the authorized tree before rerunning awctl pr.");
            if (retained.Forced && !operation.Force)
                throw Error("force_not_inherited", "This frozen intent records a force override. Continuing it requires an explicit awctl pr --force invocation.");
        }
        var review = state.Review;
        var reviewedCheckpoint = data.Passes.SingleOrDefault(p => p.Id == review?.PassId)?.ReviewCheckpoint;
        bool currentReview = review is { AuthorityValid: true, Outcome: "Clean", PublicMetadata: not null } &&
            review.SnapshotId == state.ActiveSnapshotId && review.Target == checkpoint.Target &&
            reviewedCheckpoint?.WorktreeIdentity is not null && reviewedCheckpoint.WorktreeIdentity == checkpoint.WorktreeIdentity;
        if (!operation.Force && !currentReview)
            throw Error("review_required", "The exact current tree requires a current clean independent Review Report and valid Reviewed Publication Metadata. Obtain a fresh review or explicitly use awctl pr --force.");
        if (currentReview)
        {
            try { SubmissionMetadata.Validate(review!.PublicMetadata!); }
            catch (WorkflowException) when (operation.Force) { currentReview = false; }
        }
        var submission = state.Submission;
        if (submission is null)
        {
            var task = await linear.ReadTaskAsync(state.Binding.LinearTaskId, state.Binding.LinearCredentialReference, token);
            CheckTaskIdentity(state, task);
            if (task.Identifier != data.Snapshots.Single(s => s.Id == state.ActiveSnapshotId).Identifier)
                throw Error("task_identifier_changed", "The Linear identifier no longer matches the retained task snapshot.");
            PublicSubmission metadata;
            try
            {
                // Clean review accepts the validation disclosure semantically. Preserve its exact
                // statement and the structured evidence; an English keyword list is not an eligibility gate.
                metadata = SubmissionMetadata.Create(task, currentReview ? review!.PublicMetadata : null,
                    currentReview ? review!.PassId : null, operation.Force);
            }
            catch (WorkflowException e) when (operation.Force && currentReview && e.Code == "publication_metadata_invalid")
            {
                currentReview = false;
                metadata = SubmissionMetadata.Create(task, null, null, true);
            }
            configuration.ValidateContent(CredentialReferences(state), metadata.Message + metadata.Body);
            var commit = await repository.PrepareSubmissionAsync(state, checkpoint, metadata.Message, token);
            configuration.ValidateContent(CredentialReferences(state), commit.CommitObject ?? "");
            Guid intentId = Guid.NewGuid();
            var intent = new PrIntent(intentId, state.Id, state.ActiveSnapshotId, state.Binding, checkpoint.Target,
                checkpoint.WorktreeIdentity ?? throw Error("unstable_tree", "A stable worktree fingerprint is required."),
                state.WorkspaceIntent.BaseCommit, commit.BaseBranch, state.WorkspaceIntent.Branch, checkpoint.Head,
                commit.Commit, commit.CommitObject, metadata.Message, metadata.Title, metadata.Body + $"\n<!-- AW-PR-Intent: {intentId} -->\n",
                currentReview ? review!.PassId : null, operation.Force, state.Phase,
                operation.Force ? currentReview ? "Operator explicitly selected force with current review." : "Current clean review or reviewed metadata is missing or stale." : null, Now);
            // Existing unrelated PRs must be discovered before the first publication mutation.
            if (await github.FindPullRequestAsync(intent, token) is not null)
                throw Error("existing_pull_request", "A PR already exists before this submission's first write; it cannot be adopted as this intent's result.");
            submission = new(intent, null, null, false, null);
            var warnings = operation.Force ? state.Warnings.Append(new Notice("forced_submission", intent.BypassReason!)).ToArray() : state.Warnings;
            Save(state with { Submission = submission, Phase = WorkflowPhase.Submission, Warnings = warnings,
                Attention = state.Attention.Where(a => a.Reason != AttentionReason.PassBudgetContinuation).ToArray() },
                "PrIntentFrozen", $"Intent {intent.Id} froze tree {intent.Target.ProposedTree}, base {intent.BaseCommit}, commit {intent.Commit}; preceding phase {intent.PreviousPhase}. {intent.BypassReason}");
        }
        var frozen = submission.Intent;
        state = Get(state.Id).State;
        await repository.VerifySubmissionAsync(state, frozen, token);
        Save(state, "SubmissionRunning", $"Verifying and continuing frozen intent {frozen.Id}.",
            [operation with { Status = OperationStatus.Running, ExternalActionAttempted = true }]);
        await repository.CommitSubmissionAsync(state, frozen, token);
        state = Get(state.Id).State;
        submission = state.Submission! with { Commit = frozen.Commit };
        Save(state with { Submission = submission }, "SubmissionCommitVerified",
            $"Exact commit {frozen.Commit} verified." + (frozen.OriginalHead != frozen.BaseCommit && frozen.OriginalHead != frozen.Commit
                ? $" Replaced unpublished {frozen.OriginalHead}: complete message differed; identical tree and parent preserved." : ""));
        await repository.PushSubmissionAsync(Get(state.Id).State, frozen, token);
        state = Get(state.Id).State;
        submission = state.Submission! with { PushedRevision = frozen.Commit };
        Save(state with { Submission = submission }, "SubmissionPushVerified", $"Bound remote branch verified at {frozen.Commit}.");
        var found = await github.FindPullRequestAsync(frozen, token);
        if (found is null)
        {
            if (submission.ForgeWriteAttempted)
                throw Error("forge_write_ambiguous", "A previous PR write may have succeeded, but no exact correlated PR is visible. Restore provider visibility or reconcile the intended PR manually, then rerun awctl pr. AW will not resend an ambiguous create.");
            state = Get(state.Id).State;
            await repository.PushSubmissionAsync(state, frozen, token);
            Save(state with { Submission = state.Submission! with { ForgeWriteAttempted = true } }, "PullRequestWriteStarted", $"Creating the first PR for intent {frozen.Id}.");
            try { found = await github.CreatePullRequestAsync(frozen, token); }
            catch (WorkflowException e) when (e.Code == "github_pr_rejected")
            {
                state = Get(state.Id).State;
                Save(state with { Submission = state.Submission! with { ForgeWriteAttempted = false } }, "PullRequestWriteRejected", "GitHub positively rejected creation. Retry must still reconcile before another create.");
                throw;
            }
        }
        else if (!submission.ForgeWriteAttempted)
            throw Error("uncorrelated_pull_request", "A PR appeared before AW attempted creation; its provenance cannot be tied to this intent.");
        if (submission.PullRequest is { } previous && (previous.Id != found.Id || previous.Number != found.Number))
            throw Error("pull_request_identity_changed", "The observed PR differs from the durable PR identity; no update or duplicate creation is permitted.");
        state = Get(state.Id).State;
        Save(state with { Submission = state.Submission! with { PullRequest = found }, Recovery = null },
            "SubmissionCompleted", $"PR {found.Number} verified for commit {frozen.Commit}: {found.Url}",
            [operation with { Status = OperationStatus.Completed, ExternalActionAttempted = true, FinishedAt = Now }]);
    }
}
