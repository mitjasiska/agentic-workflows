using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

public sealed record DatabaseCommand(string Action, bool Force = false, string? Backup = null);
public sealed record DatabaseReply(string State, string Message);
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
        this.database = database; this.configuration = configuration; this.repository = repository;
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

    private static async Task ObserveMaintenance(WorkflowApplication application, SqliteWorkflowStore store, CancellationToken token)
    {
        await application.ReconcileStartupAsync(token);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(token)) store.Backup(DateOnly.FromDateTime(DateTime.UtcNow));
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
            if (command.Action == "status") return new(diagnostic?.Code ?? "ready", diagnostic?.Message ?? $"Database schema {SqliteWorkflowStore.SchemaVersion} is compatible and verified.");
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
            return new("maintenance_complete", "Previous complete database archived at " + archive + ". Restart the control plane.");
        }
        finally { maintenance.Release(); }
    }
    public void MaintenanceOnly() => diagnostic = new("database_maintenance", "Workflow execution is disabled for explicit database maintenance.", "Use awctl database reset --force or awctl database restore --backup PATH, then restart without AW_DATABASE_MAINTENANCE.");
    private void EnsureAvailable()
    {
        if (diagnostic is not null) throw diagnostic;
        if (application is null) throw new WorkflowException("workflow_starting", "Workflow database admission is still starting.", "Retry the read-only query after startup completes.");
        if (background.IsFaulted) throw new WorkflowException("database_maintenance_failed", "Background integrity/backup or startup reconciliation failed; ordinary mutation is blocked.", "Inspect the database status and preserve the store before explicit recovery.");
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
