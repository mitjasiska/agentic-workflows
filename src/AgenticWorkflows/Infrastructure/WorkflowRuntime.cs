using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed record DatabaseCommand(string Action, bool Force = false, string? Backup = null);
public sealed record DatabaseReply(string State, string Message, DatabaseReadiness? Readiness = null);
public interface IWorkflowRuntime
{
    Task<WorkflowAcceptance> ExecuteAsync(WorkflowCommand command);
    Task<WorkflowQueryReply> QueryAsync(WorkflowQuery query);
    Task<DatabaseReply> DatabaseAsync(DatabaseCommand command);
}

/// <summary>Composition and database maintenance; no provider activity until schema and integrity admission succeed.</summary>
public sealed class WorkflowRuntime : IWorkflowRuntime, IAsyncDisposable
{
    private readonly string database;
    private readonly IWorkflowConfiguration configuration;
    private readonly IWorkflowRepository repository;
    private readonly ILinearGateway linear;
    private readonly IGitHubGateway github;
    private readonly IHerdrGateway herdr;
    private readonly IPassDelivery delivery;
    private readonly SemaphoreSlim maintenance = new(1, 1);
    private SqliteWorkflowStore? store;
    private WorkflowApplication? application;
    private WorkflowQueries? queries;
    private WorkflowException? diagnostic;
    private readonly CancellationTokenSource stopping = new();
    private Task background = Task.CompletedTask;
    private int disposed;

    public WorkflowRuntime(string database, IWorkflowConfiguration configuration, IWorkflowRepository repository,
        ILinearGateway linear, IGitHubGateway github, IHerdrGateway herdr, IPassDelivery delivery)
    {
        this.database = Path.GetFullPath(database); this.configuration = configuration; this.repository = repository;
        this.linear = linear; this.github = github; this.herdr = herdr; this.delivery = delivery;
    }

    public void Start()
    {
        try
        {
            store = new(database);
            store.Backup(DateOnly.FromDateTime(DateTime.UtcNow));
            application = new(store, configuration, linear, github, herdr, repository, delivery);
            queries = new(application, repository);
            diagnostic = null;
            background = ObserveMaintenance(application, store, stopping.Token);
        }
        catch (WorkflowException exception) { diagnostic = exception; }
    }

    private async Task ObserveMaintenance(WorkflowApplication application, SqliteWorkflowStore store, CancellationToken token)
    {
        try
        {
            await application.ReconcileStartupAsync(token);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            while (await timer.WaitForNextTickAsync(token)) store.Backup(DateOnly.FromDateTime(DateTime.UtcNow));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (WorkflowException error) { diagnostic = error; }
        catch (Exception) { diagnostic = new("workflow_reconciliation_failed", $"Startup reconciliation or backup observation failed for '{database}'.", "Inspect service logs and database status; preserve retained work and restart after resolving the failure."); }
    }

    public DatabaseReadiness Inspect()
    {
        var activeFailure = store?.AdmissionFailure;
        var error = activeFailure ?? diagnostic;
        try { SqliteMaintenance.RequireComplete(database); }
        catch (WorkflowException pending) { error = pending; }
        long? observed = SqliteWorkflowStore.ObserveSchema(database);
        if (observed is long version && version != SqliteWorkflowStore.SchemaVersion && error?.Code != "database_maintenance")
        {
            var mismatch = SqliteWorkflowStore.SchemaIncompatibility(database, version);
            error = new(mismatch.Code, $"active database '{database}': {mismatch.Message}", mismatch.Guidance);
        }
        bool admitted = store is not null && activeFailure is null && observed == SqliteWorkflowStore.SchemaVersion && error?.Code != "database_maintenance";
        return new(error?.Code ?? (application is null ? "workflow_starting" : "ready"), database,
            SqliteWorkflowStore.SchemaVersion, observed, admitted,
            admitted && error is null && application is not null, error?.Message ?? (application is null ? "Database admission is starting." : "Active database and current backups are compatible and verified."), error?.Guidance);
    }

    public Task<WorkflowAcceptance> ExecuteAsync(WorkflowCommand command)
    {
        EnsureAvailable();
        return application!.ExecuteAsync(command, stopping.Token);
    }
    public async Task<WorkflowQueryReply> QueryAsync(WorkflowQuery query)
    {
        EnsureAvailable();
        return await queries!.ExecuteAsync(query, stopping.Token);
    }
    public async Task<DatabaseReply> DatabaseAsync(DatabaseCommand command)
    {
        await maintenance.WaitAsync();
        try
        {
            if (command.Action == "status") { var status = Inspect(); return new(status.State, status.Message, status); }
            if (application is not null)
                throw new WorkflowException("database_in_use", "Reset and restore require maintenance startup with the ordinary workflow runtime stopped.",
                    "Stop the host and restart it with AW_DATABASE_MAINTENANCE=1, then issue the explicit database reset or restore command.");
            string archive = command.Action switch
            {
                "reset" => SqliteWorkflowStore.Reset(database, command.Force),
                "restore" when command.Backup is not null => SqliteWorkflowStore.Restore(database, command.Backup),
                _ => throw new WorkflowException("invalid_database_command", "Expected reset --force or restore --backup PATH.", "No database files were changed.")
            };
            diagnostic = new("restart_required", "Database maintenance completed. Restart the control plane for ordinary reconciliation.", "Remove AW_DATABASE_MAINTENANCE and restart the host.");
            store = null;
            return new("maintenance_complete", "Previous complete database archived at " + archive + ". Restart the control plane.");
        }
        finally { maintenance.Release(); }
    }
    public void MaintenanceOnly() => diagnostic = new("database_maintenance", "Workflow execution is disabled for explicit database maintenance.", "Use awctl database reset --force or awctl database restore --backup PATH, then restart without AW_DATABASE_MAINTENANCE.");
    private void EnsureAvailable()
    {
        if (diagnostic is not null) throw diagnostic;
        if (application is null) throw new WorkflowException("workflow_starting", "Workflow database admission is still starting.", "Retry the read-only query after startup completes.");
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await stopping.CancelAsync();
        if (application is not null) await application.DisposeAsync();
        try { await background; } catch (OperationCanceledException) { }
        stopping.Dispose();
    }
}
