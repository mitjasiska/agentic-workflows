using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Application;
using AgenticWorkflows.LocalControl;
using AgenticWorkflows.Workflows;
using Microsoft.Data.Sqlite;

namespace AgenticWorkflows.Tests;

public sealed class DatabaseLifecycleTests
{
    [Fact]
    public async Task Incompatible_active_and_same_day_backup_reset_restart_allows_project_setup()
    {
        string root = Directory.CreateTempSubdirectory("aw-database-lifecycle-").FullName;
        try
        {
            string path = Path.Combine(root, "workflows.db");
            _ = new SqliteWorkflowStore(path);
            SetVersion(path, 2);
            // Both files really contain schema 2, as in the reported upgrade sequence.
            Directory.CreateDirectory(path + ".daily");
            string backup = Path.Combine(path + ".daily", DateTime.UtcNow.ToString("yyyy-MM-dd") + ".db");
            File.Copy(path, backup);
            var providers = new ControlledProviders(root);
            await using (var before = Runtime(path, providers))
            {
                before.Start();
                Assert.Equal("schema_incompatible", (await before.DatabaseAsync(new("status"))).State);
                await before.DatabaseAsync(new("reset", Force: true));
            }
            await using var restarted = Runtime(path, providers);
            restarted.Start();
            await using var socket = new SocketFixture();
            await using var server = new LocalControlServer(new ControlPlaneStatusQuery(restarted.Inspect), socket.Endpoint, restarted);
            await server.StartAsync();
            Assert.True((await socket.Client().StatusAsync()).Status.Database!.WorkflowReady);
            await socket.Client().CommandAsync(new("setup", root));
            Assert.NotNull(new SqliteWorkflowStore(path).FindProject(root));
            string archive = Assert.Single(Directory.GetDirectories(root, "workflows.db.archive-*"));
            Assert.True(File.Exists(Path.Combine(archive, "workflows.db.daily", Path.GetFileName(backup))));
            Assert.Equal(2, Version(Path.Combine(archive, "workflows.db")));
            Assert.Equal(SqliteWorkflowStore.SchemaVersion, Version(backup));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Fresh_start_and_compatible_restart_report_readiness_and_preserve_project()
    {
        using var f = new Fixture();
        await using (var runtime = Runtime(f.Path, f.Providers))
        {
            Assert.Equal("workflow_starting", runtime.Inspect().State);
            runtime.Start();
            var status = (await runtime.DatabaseAsync(new("status"))).Readiness!;
            Assert.True(status.DatabaseReady); Assert.True(status.WorkflowReady);
            Assert.Equal(SqliteWorkflowStore.SchemaVersion, status.SupportedSchema);
            Assert.Equal((long)status.SupportedSchema, status.ActiveSchema); Assert.Equal(f.Path, status.Path);
            await runtime.ExecuteAsync(new("setup", f.Root));
            Assert.Equal("database_in_use", (await Assert.ThrowsAsync<WorkflowException>(() => runtime.DatabaseAsync(new("reset", true)))).Code);
        }
        await using var restart = Runtime(f.Path, f.Providers);
        restart.Start();
        await restart.ExecuteAsync(new("setup", f.Root));
        Assert.NotNull(new SqliteWorkflowStore(f.Path).FindProject(f.Root));
        Assert.Single(Directory.GetFiles(f.Path + ".daily", "*.db"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(999)]
    public async Task Incompatible_active_health_is_reachable_and_reports_versions_without_provider_work(int version)
    {
        using var f = new Fixture(); _ = new SqliteWorkflowStore(f.Path); SetVersion(f.Path, version);
        byte[] before = File.ReadAllBytes(f.Path);
        await using var runtime = Runtime(f.Path, f.Providers); runtime.Start();
        await using var socket = new SocketFixture();
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(runtime.Inspect), socket.Endpoint, runtime);
        await server.StartAsync();
        var health = (await socket.Client().StatusAsync()).Status;
        Assert.Equal("running", health.State);
        var status = (await socket.Client().DatabaseAsync(new("status"))).Database.Readiness!;
        Assert.Equal("schema_incompatible", status.State);
        Assert.Equal((long)version, status.ActiveSchema); Assert.Equal(SqliteWorkflowStore.SchemaVersion, status.SupportedSchema);
        Assert.False(status.DatabaseReady); Assert.False(status.WorkflowReady); Assert.Contains(f.Path, status.Message);
        var error = await Assert.ThrowsAsync<LocalControlException>(() => socket.Client().CommandAsync(new("setup", f.Root)));
        Assert.Equal("schema_incompatible", error.Diagnostic.Code);
        Assert.Empty(f.Providers.Calls); Assert.Equal(before, File.ReadAllBytes(f.Path));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(999, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(999, false)]
    public async Task Inspection_reports_observed_schema_incompatibility_before_any_workflow_request(int version, bool healthFirst)
    {
        using var f = new Fixture();
        await using var runtime = Runtime(f.Path, f.Providers); runtime.Start();
        Assert.True(runtime.Inspect().WorkflowReady);
        await using var socket = new SocketFixture();
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(runtime.Inspect), socket.Endpoint, runtime);
        await server.StartAsync();
        SetVersion(f.Path, version);
        byte[] before = File.ReadAllBytes(f.Path);
        var backups = Directory.GetFiles(f.Path + ".daily", "*.db").ToDictionary(file => file, File.ReadAllBytes);
        var client = socket.Client();
        DatabaseReply status;
        ControlPlaneStatus health;
        if (healthFirst)
        {
            health = (await client.StatusAsync()).Status;
            status = (await client.DatabaseAsync(new("status"))).Database;
        }
        else
        {
            status = (await client.DatabaseAsync(new("status"))).Database;
            health = (await client.StatusAsync()).Status;
        }
        Assert.Equal("running", health.State);
        Assert.Equal("schema_incompatible", status.State);
        foreach (var readiness in new[] { status.Readiness!, health.Database! })
        {
            Assert.Equal("schema_incompatible", readiness.State);
            Assert.False(readiness.DatabaseReady); Assert.False(readiness.WorkflowReady);
            Assert.Equal((long)version, readiness.ActiveSchema); Assert.Equal(SqliteWorkflowStore.SchemaVersion, readiness.SupportedSchema);
            Assert.Equal(f.Path, readiness.Path); Assert.Contains(f.Path, readiness.Message);
            Assert.Contains($"schema {version}", readiness.Message);
            Assert.Contains($"supported schema {SqliteWorkflowStore.SchemaVersion}", readiness.Message);
            Assert.Contains("compatible build", readiness.Guidance);
            Assert.Contains("awctl database reset --force", readiness.Guidance);
        }
        Assert.Equal(status.Readiness!.Message, status.Message);
        Assert.Equal(status.Readiness, health.Database);
        Assert.Empty(f.Providers.Calls); Assert.Equal(before, File.ReadAllBytes(f.Path));
        foreach (var (file, bytes) in backups) Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Active_schema_observation_reports_current_versions_instead_of_a_prior_backup_failure()
    {
        using var f = new Fixture();
        var store = new SqliteWorkflowStore(f.Path); store.Backup(DateOnly.FromDateTime(DateTime.UtcNow));
        string backup = Directory.GetFiles(f.Path + ".daily", "*.db").Single(); SetVersion(backup, 2);
        await using var runtime = Runtime(f.Path, f.Providers); runtime.Start();
        Assert.Contains(backup, runtime.Inspect().Message);
        SetVersion(f.Path, 999);
        var status = (await runtime.DatabaseAsync(new("status"))).Readiness!;
        Assert.Equal("schema_incompatible", status.State);
        Assert.Equal(999L, status.ActiveSchema); Assert.Equal(SqliteWorkflowStore.SchemaVersion, status.SupportedSchema);
        Assert.Contains(f.Path, status.Message); Assert.DoesNotContain(backup, status.Message);
        Assert.Contains("schema 999", status.Message); Assert.Contains("supported schema 3", status.Message);
        Assert.Contains("compatible build", status.Guidance);
        Assert.False(status.DatabaseReady); Assert.False(status.WorkflowReady);
        Assert.Empty(f.Providers.Calls);
    }

    [Fact]
    public async Task Maintenance_inspection_keeps_continuation_guidance_with_an_incompatible_active_schema()
    {
        using var f = new Fixture(); _ = new SqliteWorkflowStore(f.Path); SetVersion(f.Path, 2);
        byte[] before = File.ReadAllBytes(f.Path);
        await using var runtime = Runtime(f.Path, f.Providers); runtime.MaintenanceOnly();
        var status = (await runtime.DatabaseAsync(new("status"))).Readiness!;
        Assert.Equal("database_maintenance", status.State);
        Assert.Equal(2L, status.ActiveSchema); Assert.Equal(SqliteWorkflowStore.SchemaVersion, status.SupportedSchema);
        Assert.False(status.DatabaseReady); Assert.False(status.WorkflowReady);
        Assert.Contains("awctl database reset --force", status.Guidance);
        Assert.Equal(before, File.ReadAllBytes(f.Path)); Assert.Empty(f.Providers.Calls);
    }

    [Fact]
    public async Task Current_backup_failure_reports_its_own_path_while_active_schema_remains_compatible()
    {
        using var f = new Fixture();
        var store = new SqliteWorkflowStore(f.Path);
        store.Backup(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
        string backup = Directory.GetFiles(f.Path + ".daily", "*.db").Single(); SetVersion(backup, 2);
        await using var runtime = Runtime(f.Path, f.Providers); runtime.Start();
        var status = runtime.Inspect();
        Assert.Equal("schema_incompatible", status.State); Assert.Contains("current daily backup", status.Message);
        Assert.Contains(backup, status.Message); Assert.Equal((long)SqliteWorkflowStore.SchemaVersion, status.ActiveSchema);
        Assert.True(status.DatabaseReady); Assert.False(status.WorkflowReady); Assert.Empty(f.Providers.Calls);
        Assert.Equal(2, Version(backup));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Corrupt_current_backup_is_preserved_and_explicit_reset_recovers(bool sameDay)
    {
        using var f = new Fixture(); var store = new SqliteWorkflowStore(f.Path);
        store.Backup(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(sameDay ? 0 : -10));
        string backup = Directory.GetFiles(f.Path + ".daily", "*.db").Single(); File.WriteAllText(backup, "damaged backup");
        await using var runtime = Runtime(f.Path, f.Providers); runtime.Start();
        var status = runtime.Inspect(); Assert.Equal("database_corrupt", status.State);
        Assert.Contains(backup, status.Message); Assert.True(status.DatabaseReady); Assert.False(status.WorkflowReady);
        Assert.Contains("current daily backup", status.Message);
        await runtime.DatabaseAsync(new("reset", true));
        string archive = Directory.GetDirectories(f.Root, "*.archive-*").Single();
        Assert.Equal("damaged backup", File.ReadAllText(Path.Combine(archive, "workflows.db.daily", Path.GetFileName(backup))));
        await using var restarted = Runtime(f.Path, f.Providers); restarted.Start();
        await restarted.ExecuteAsync(new("setup", f.Root)); Assert.True(restarted.Inspect().WorkflowReady);
    }

    [Fact]
    public async Task Maintenance_only_commands_can_be_repeated_and_preserve_previous_archives()
    {
        using var f = new Fixture();
        await using var runtime = Runtime(f.Path, f.Providers); runtime.MaintenanceOnly();
        Assert.False(File.Exists(f.Path));
        Assert.Equal("database_maintenance", (await runtime.DatabaseAsync(new("status"))).State);
        Assert.Equal("force_required", (await Assert.ThrowsAsync<WorkflowException>(() => runtime.DatabaseAsync(new("reset")))).Code);
        Assert.False(File.Exists(f.Path));
        await runtime.DatabaseAsync(new("reset", true));
        byte[] before = File.ReadAllBytes(f.Path);
        await runtime.DatabaseAsync(new("reset", true));
        Assert.Equal(2, Directory.GetDirectories(f.Root, "*.archive-*").Length);
        Assert.Contains(Directory.GetDirectories(f.Root, "*.archive-*"), archive =>
            File.Exists(Path.Combine(archive, "workflows.db")) && before.SequenceEqual(File.ReadAllBytes(Path.Combine(archive, "workflows.db"))));
        Assert.Equal("restart_required", runtime.Inspect().State);
        Assert.False(runtime.Inspect().WorkflowReady); Assert.Empty(f.Providers.Calls);
    }

    [Fact]
    public void Incomplete_unpublished_preparation_does_not_change_admission_or_require_manual_removal()
    {
        using var f = new Fixture(); _ = new SqliteWorkflowStore(f.Path);
        string staging = f.Path + ".prepare-interrupted"; Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "workflows.db"), "partial preparation");
        var store = new SqliteWorkflowStore(f.Path); store.Backup(DateOnly.FromDateTime(DateTime.UtcNow));
        SqliteWorkflowStore.Reset(f.Path, true);
        Assert.Equal("partial preparation", File.ReadAllText(Path.Combine(staging, "workflows.db")));
        SqliteWorkflowStore.VerifyFile(f.Path);
    }

    [Fact]
    public void Locked_or_missing_restore_selection_is_not_classified_as_corruption_and_preserves_current_store()
    {
        using var f = new Fixture(); _ = new SqliteWorkflowStore(f.Path);
        string source = Path.Combine(f.Root, "selected.db"); _ = new SqliteWorkflowStore(source);
        byte[] before = File.ReadAllBytes(f.Path);
        using (var blocker = Connect(source))
        {
            using var command = blocker.CreateCommand(); command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
            try
            {
                var error = Assert.Throws<WorkflowException>(() => SqliteWorkflowStore.Restore(f.Path, source));
                Assert.Equal("database_busy", error.Code); Assert.Contains(source, error.Message);
                Assert.Equal(before, File.ReadAllBytes(f.Path)); Assert.Empty(Directory.GetDirectories(f.Root, "*.archive-*"));
            }
            finally { command.CommandText = "ROLLBACK"; command.ExecuteNonQuery(); }
        }
        var missing = Assert.Throws<WorkflowException>(() => SqliteWorkflowStore.Restore(f.Path, source + ".missing"));
        Assert.Equal("database_access", missing.Code); Assert.Equal(before, File.ReadAllBytes(f.Path));
        SqliteWorkflowStore.Restore(f.Path, source); SqliteWorkflowStore.VerifyFile(f.Path);
    }

    [Fact]
    public void Reset_preserves_complete_old_set_and_excludes_historical_corruption_from_admission_and_retention()
    {
        using var f = new Fixture(); var old = new SqliteWorkflowStore(f.Path);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = -8; i <= 0; i++) old.Backup(day.AddDays(i));
        var files = Directory.GetFiles(f.Path + ".daily", "*.db").ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        string historical = f.Path + ".archive-historical"; Directory.CreateDirectory(historical);
        File.WriteAllText(Path.Combine(historical, "corrupt.db"), "historical material");
        File.WriteAllText(Path.Combine(f.Path + ".daily", "operator-note"), "preserved backup note");
        var unrelated = new[] { "global.toml", "credentials", "repo/file", "worktree/file", "herdr/resource", "unrelated.db" };
        foreach (string file in unrelated)
        {
            string target = Path.Combine(f.Root, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "preserved");
        }
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) File.WriteAllText(f.Path + suffix, "old companion " + suffix);
        string archive = SqliteWorkflowStore.Reset(f.Path, true);
        foreach (var (name, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(archive, "workflows.db.daily", name!)));
        Assert.Equal("preserved backup note", File.ReadAllText(Path.Combine(archive, "workflows.db.daily", "operator-note")));
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) Assert.Equal("old companion " + suffix, File.ReadAllText(Path.Combine(archive, "workflows.db" + suffix)));
        var fresh = new SqliteWorkflowStore(f.Path); fresh.Backup(day);
        for (int i = 1; i < 9; i++) fresh.Backup(day.AddDays(i));
        Assert.Equal(7, Directory.GetFiles(f.Path + ".daily", "*.db").Length);
        Assert.Equal(7, Directory.GetFiles(Path.Combine(archive, "workflows.db.daily"), "*.db").Length);
        Assert.Equal("historical material", File.ReadAllText(Path.Combine(historical, "corrupt.db")));
        Assert.All(unrelated, file => Assert.Equal("preserved", File.ReadAllText(Path.Combine(f.Root, file))));
    }

    [Fact]
    public void Restore_source_in_current_set_is_staged_before_that_set_is_archived()
    {
        using var f = new Fixture(); var store = new SqliteWorkflowStore(f.Path);
        Execute(f.Path, "CREATE TABLE recovery_probe(value TEXT); INSERT INTO recovery_probe VALUES('selected state');");
        store.Backup(DateOnly.FromDateTime(DateTime.UtcNow));
        string source = Directory.GetFiles(f.Path + ".daily", "*.db").Single(); byte[] selected = File.ReadAllBytes(source);
        Execute(f.Path, "UPDATE recovery_probe SET value='newer current state'");
        byte[] current = File.ReadAllBytes(f.Path);
        string archive = SqliteWorkflowStore.Restore(f.Path, source);
        Assert.Equal("selected state", Scalar(f.Path, "SELECT value FROM recovery_probe"));
        Assert.Equal(current, File.ReadAllBytes(Path.Combine(archive, "workflows.db")));
        Assert.Equal(selected, File.ReadAllBytes(Path.Combine(archive, "workflows.db.daily", Path.GetFileName(source))));
        Assert.Equal("selected state", Scalar(Directory.GetFiles(f.Path + ".daily", "*.db").Single(), "SELECT value FROM recovery_probe"));
        Assert.Equal(archive, Directory.GetDirectories(f.Root, "*.archive-*").Single());
    }

    [Theory]
    [InlineData(3, "prepared")]
    [InlineData(2, "prepared")]
    [InlineData(3, "archive.daily")]
    [InlineData(2, "archive.daily")]
    public void Interrupted_restore_rejects_a_different_selection_and_continues_the_original(int otherSchema, string step)
    {
        using var f = new Fixture(); var store = new SqliteWorkflowStore(f.Path);
        Execute(f.Path, "CREATE TABLE recovery_probe(value TEXT); INSERT INTO recovery_probe VALUES('original selection');");
        store.Backup(DateOnly.FromDateTime(DateTime.UtcNow));
        string source = Directory.GetFiles(f.Path + ".daily", "*.db").Single();
        string other = Path.Combine(f.Root, "other.db"); File.Copy(source, other);
        Execute(other, "UPDATE recovery_probe SET value='different selection'"); SetVersion(other, otherSchema);
        Execute(f.Path, "UPDATE recovery_probe SET value='current'");
        byte[] current = File.ReadAllBytes(f.Path), different = File.ReadAllBytes(other);
        string relativeSelection = Path.GetRelativePath(Environment.CurrentDirectory, source);
        Assert.Throws<SimulatedInterruption>(() => SqliteMaintenance.Replace(f.Path, relativeSelection,
            point => { if (point == step) throw new SimulatedInterruption(); }));
        var pending = Directory.GetFiles(f.Path + ".maintenance", "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(f.Path + ".maintenance", file), File.ReadAllBytes);

        var error = Assert.Throws<WorkflowException>(() => SqliteWorkflowStore.Restore(f.Path, other));
        Assert.Equal("database_maintenance", error.Code);
        Assert.Contains(other, error.Message); Assert.Contains(source, error.Guidance);
        Assert.Contains("awctl database restore --backup", error.Guidance);
        Assert.Equal(different, File.ReadAllBytes(other));
        Assert.Equal(pending.Count, Directory.GetFiles(f.Path + ".maintenance", "*", SearchOption.AllDirectories).Length);
        foreach (var (name, bytes) in pending) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Path + ".maintenance", name)));

        // A normalized spelling of the original selection resumes without reopening a source
        // that may already have moved into its historical archive.
        string equivalent = Path.Combine(Path.GetDirectoryName(source)!, ".", Path.GetFileName(source));
        string archive = SqliteWorkflowStore.Restore(f.Path, equivalent);
        Assert.Equal("original selection", Scalar(f.Path, "SELECT value FROM recovery_probe"));
        Assert.Equal(current, File.ReadAllBytes(Path.Combine(archive, "workflows.db")));
        Assert.Equal("original selection", Scalar(Path.Combine(archive, "workflows.db.daily", Path.GetFileName(source)), "SELECT value FROM recovery_probe"));
        Assert.Equal(different, File.ReadAllBytes(other));
        Assert.False(Directory.Exists(f.Path + ".maintenance"));
    }

    [Theory]
    [InlineData("command", "structure", "database_structure_invalid")]
    [InlineData("query", "structure", "database_structure_invalid")]
    [InlineData("command", "schema", "schema_incompatible")]
    [InlineData("query", "schema", "schema_incompatible")]
    [InlineData("command", "corrupt", "database_corrupt")]
    [InlineData("query", "corrupt", "database_corrupt")]
    public async Task Detected_active_store_failure_is_reported_by_database_status_and_health(string request, string damage, string code)
    {
        using var f = new Fixture();
        await using var runtime = Runtime(f.Path, f.Providers); runtime.Start();
        Assert.True(runtime.Inspect().WorkflowReady);
        if (damage == "structure") Execute(f.Path, "DROP TRIGGER immutable_snapshot");
        if (damage == "schema") SetVersion(f.Path, 2);
        if (damage == "corrupt") File.WriteAllText(f.Path, "corrupted active store");
        byte[] damaged = File.ReadAllBytes(f.Path);
        await using var socket = new SocketFixture();
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(runtime.Inspect), socket.Endpoint, runtime);
        await server.StartAsync();
        var client = socket.Client();
        var error = await Assert.ThrowsAsync<LocalControlException>(async () =>
        {
            if (request == "command") await client.CommandAsync(new("setup", f.Root));
            else await client.QueryAsync(new(f.Root, View: "list"));
        });
        Assert.Equal(code, error.Diagnostic.Code);
        var status = (await client.DatabaseAsync(new("status"))).Database;
        var health = (await client.StatusAsync()).Status;
        Assert.Equal("running", health.State);
        foreach (var readiness in new[] { status.Readiness!, health.Database! })
        {
            Assert.Equal(code, readiness.State); Assert.False(readiness.DatabaseReady); Assert.False(readiness.WorkflowReady);
            Assert.Equal(error.Diagnostic.Message, readiness.Message); Assert.Equal(error.Diagnostic.Guidance, readiness.Guidance);
            Assert.Contains(f.Path, readiness.Message); Assert.Equal(f.Path, readiness.Path);
            Assert.Equal(SqliteWorkflowStore.SchemaVersion, readiness.SupportedSchema);
            Assert.Equal(damage == "corrupt" ? (long?)null : damage == "schema" ? 2L : SqliteWorkflowStore.SchemaVersion, readiness.ActiveSchema);
        }
        Assert.Equal(code, status.State); Assert.Equal(damaged, File.ReadAllBytes(f.Path));
    }

    [Theory]
    [InlineData("busy", "database_busy")]
    [InlineData("access", "database_access")]
    public async Task Transient_active_admission_failure_is_cleared_only_by_a_successful_database_retry(string failure, string code)
    {
        using var f = new Fixture();
        await using var runtime = Runtime(f.Path, f.Providers); runtime.Start();
        using var blocker = Connect(f.Path);
        using var command = blocker.CreateCommand();
        if (failure == "busy") { command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery(); }
        else File.Move(f.Path, f.Path + ".temporarily-inaccessible");
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowException>(() => runtime.QueryAsync(new(f.Root, View: "list")));
            Assert.Equal(code, error.Code);
            var readiness = (await runtime.DatabaseAsync(new("status"))).Readiness!;
            Assert.Equal(code, readiness.State); Assert.False(readiness.DatabaseReady); Assert.False(readiness.WorkflowReady);
        }
        finally
        {
            if (failure == "busy") { command.CommandText = "ROLLBACK"; command.ExecuteNonQuery(); }
            else File.Move(f.Path + ".temporarily-inaccessible", f.Path);
        }
        // An inspection of user_version alone cannot erase a known failed integrity admission.
        Assert.Equal(code, runtime.Inspect().State);
        await runtime.QueryAsync(new(f.Root, View: "list"));
        Assert.Equal("ready", runtime.Inspect().State); Assert.True(runtime.Inspect().DatabaseReady); Assert.True(runtime.Inspect().WorkflowReady);
    }

    [Theory]
    [InlineData("linear_unavailable")]
    [InlineData("database_structure_invalid")]
    public async Task Provider_failure_does_not_change_active_database_or_workflow_readiness(string code)
    {
        using var f = new Fixture();
        await using var runtime = new WorkflowRuntime(f.Path, f.Providers, f.Providers, new RejectingLinear(f.Providers, code),
            f.Providers, f.Providers, new PassDelivery());
        runtime.Start();
        await using var socket = new SocketFixture();
        await using var server = new LocalControlServer(new ControlPlaneStatusQuery(runtime.Inspect), socket.Endpoint, runtime);
        await server.StartAsync();
        var error = await Assert.ThrowsAsync<LocalControlException>(() => socket.Client().CommandAsync(new("setup", f.Root)));
        Assert.Equal(code, error.Diagnostic.Code);
        foreach (var readiness in new[] { (await socket.Client().DatabaseAsync(new("status"))).Database.Readiness!, (await socket.Client().StatusAsync()).Status.Database! })
        {
            Assert.Equal("ready", readiness.State); Assert.True(readiness.DatabaseReady); Assert.True(readiness.WorkflowReady);
        }
    }

    private sealed class RejectingLinear(ControlledProviders inner, string code) : ILinearGateway
    {
        public Task<LinearTask> ReadTaskAsync(string id, string credential, CancellationToken token) => inner.ReadTaskAsync(id, credential, token);
        public Task MoveToInProgressAsync(IntegrationBinding binding, CancellationToken token) => inner.MoveToInProgressAsync(binding, token);
        public Task<(ProviderProjection Project, ProviderProjection Team, ProviderProjection State)> ValidateProjectAsync(
            string project, string team, string? state, string credential, CancellationToken token) =>
            throw new WorkflowException(code, "Controlled provider failure", "Restore the controlled provider.");
    }

    [Theory]
    [InlineData("schema", "schema_incompatible")]
    [InlineData("corrupt", "database_corrupt")]
    [InlineData("foreign-key", "database_corrupt")]
    [InlineData("forged", "database_structure_invalid")]
    [InlineData("trigger", "database_structure_invalid")]
    public void Invalid_restore_is_refused_without_archiving_or_changing_any_material(string kind, string code)
    {
        using var f = new Fixture(); _ = new SqliteWorkflowStore(f.Path);
        string source = Path.Combine(f.Root, "2099-12-31.db"); _ = new SqliteWorkflowStore(source);
        if (kind == "schema") SetVersion(source, 2);
        if (kind == "corrupt") File.WriteAllText(source, "corrupted SQLite material");
        if (kind == "foreign-key") Execute(source, "PRAGMA foreign_keys=OFF; INSERT INTO tasks(id,project_id,provider_id) VALUES('orphan','missing','task');");
        if (kind == "forged") Execute(source, "DROP TABLE submissions");
        if (kind == "trigger") Execute(source, "DROP TRIGGER immutable_snapshot");
        byte[] current = File.ReadAllBytes(f.Path), selected = File.ReadAllBytes(source);
        var error = Assert.Throws<WorkflowException>(() => SqliteWorkflowStore.Restore(f.Path, source));
        Assert.Equal(code, error.Code); Assert.Contains("selected restore backup", error.Message); Assert.Contains(source, error.Message);
        if (kind == "schema") { Assert.Contains("schema 2", error.Message); Assert.Contains("schema 3", error.Message); Assert.Contains("compatible build", error.Guidance); }
        Assert.Equal(current, File.ReadAllBytes(f.Path)); Assert.Equal(selected, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetDirectories(f.Root, "*.archive-*")); Assert.False(Directory.Exists(f.Path + ".maintenance"));
    }

    public static TheoryData<string, bool> Interruptions
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string step in new[] { "prepared", "archive", "archive-wal", "archive-shm", "archive-journal", "archive.daily", "archived", "installed-database", "installed-backups" })
                foreach (bool restore in new[] { false, true }) data.Add(step, restore);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Interruptions))]
    public void Interrupted_maintenance_blocks_admission_and_repeating_same_command_finishes_without_losing_old_set(string step, bool restore)
    {
        using var f = new Fixture(); var store = new SqliteWorkflowStore(f.Path);
        Execute(f.Path, "CREATE TABLE recovery_probe(value TEXT); INSERT INTO recovery_probe VALUES('selected');");
        store.Backup(DateOnly.FromDateTime(DateTime.UtcNow));
        string source = Directory.GetFiles(f.Path + ".daily", "*.db").Single(); byte[] backup = File.ReadAllBytes(source);
        Execute(f.Path, "UPDATE recovery_probe SET value='current'");
        byte[] old = File.ReadAllBytes(f.Path);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) File.WriteAllText(f.Path + suffix, "retained " + suffix);
        // Restore selection is verified before any file is moved; reset never opens the old companions.
        Assert.Throws<SimulatedInterruption>(() => SqliteMaintenance.Replace(f.Path, restore ? source : null,
            point => { if (point == step) throw new SimulatedInterruption(); }));
        Assert.Equal("database_maintenance", Assert.Throws<WorkflowException>(() => new SqliteWorkflowStore(f.Path)).Code);
        Assert.Equal("database_maintenance", Assert.Throws<WorkflowException>(() =>
        { _ = restore ? SqliteWorkflowStore.Reset(f.Path, true) : SqliteWorkflowStore.Restore(f.Path, source); }).Code);
        string archive = restore ? SqliteWorkflowStore.Restore(f.Path, source) : SqliteWorkflowStore.Reset(f.Path, true);
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(archive, "workflows.db")));
        Assert.Equal(backup, File.ReadAllBytes(Path.Combine(archive, "workflows.db.daily", Path.GetFileName(source))));
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" }) Assert.Equal("retained " + suffix, File.ReadAllText(Path.Combine(archive, "workflows.db" + suffix)));
        Assert.False(Directory.Exists(f.Path + ".maintenance")); SqliteWorkflowStore.VerifyFile(f.Path);
        new SqliteWorkflowStore(f.Path).Backup(DateOnly.FromDateTime(DateTime.UtcNow));
        if (restore) Assert.Equal("selected", Scalar(f.Path, "SELECT value FROM recovery_probe"));
        Assert.Single(Directory.GetDirectories(f.Root, "*.archive-*"));
    }

    private sealed class SimulatedInterruption : Exception;
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("aw-db-test-").FullName;
        internal string Path => System.IO.Path.Combine(Root, "workflows.db");
        internal ControlledProviders Providers { get; }
        internal Fixture() => Providers = new(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }

    internal static void Execute(string path, string sql)
    { using var db = Connect(path); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static object? Scalar(string path, string sql)
    { using var db = Connect(path); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }

    internal static WorkflowRuntime Runtime(string path, ControlledProviders providers) =>
        new(path, providers, providers, providers, providers, providers, new PassDelivery());

    internal static void SetVersion(string path, int version)
    {
        using var db = Connect(path);
        using var command = db.CreateCommand(); command.CommandText = $"PRAGMA user_version={version}"; command.ExecuteNonQuery();
    }
    internal static long Version(string path)
    {
        using var db = Connect(path);
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version"; return (long)command.ExecuteScalar()!;
    }
    internal static SqliteConnection Connect(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); return db;
    }
}
