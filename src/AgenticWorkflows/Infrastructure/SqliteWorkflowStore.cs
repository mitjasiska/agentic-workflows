using System.Text.Json;
using System.Security.Cryptography;
using AgenticWorkflows.Application;
using AgenticWorkflows.Workflows;
using Microsoft.Data.Sqlite;

namespace AgenticWorkflows.Infrastructure;

/// <summary>One transactional store: current state is read directly; events explain it without replay.</summary>
public sealed class SqliteWorkflowStore : IWorkflowStore
{
    public const int SchemaVersion = 3;
    private readonly string path;
    private readonly object gate = new();
    private bool corrupt;
    private bool admitted;
    private WorkflowException? admissionFailure;
    internal WorkflowException? AdmissionFailure => Volatile.Read(ref admissionFailure);
    private static readonly JsonSerializerOptions Json = ResultEnvelopeContract.Json;

    public SqliteWorkflowStore(string path)
    {
        this.path = Path.GetFullPath(path);
        SqliteMaintenance.RequireComplete(this.path);
        WithResource("active database", this.path, () => { Initialize(); return true; });
    }

    private void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        if (File.Exists(this.path)) AdmitFile(this.path);
        using var connection = Open();
        if (Scalar<long>(connection, "SELECT count(*) FROM sqlite_master WHERE type='table'") == 0)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, Schema, transaction);
            transaction.Commit();
        }
        VerifyFile(this.path);
        admitted = true;
    }

    public ProjectRegistration? FindProject(string canonicalCheckout)
    {
        using var db = Open();
        return ReadOne<ProjectRegistration>(db, "SELECT json FROM projects WHERE checkout=$id", canonicalCheckout);
    }

    public void RegisterProject(ProjectRegistration project)
    {
        lock (gate)
        {
            using var db = Open();
            Execute(db, "INSERT INTO projects(id,checkout,git_directory,json) VALUES($id,$checkout,$git,$json) " +
                "ON CONFLICT(id) DO UPDATE SET json=excluded.json WHERE checkout=excluded.checkout AND git_directory=excluded.git_directory",
                null, ("id", project.Id), ("checkout", project.CanonicalCheckout), ("git", project.GitDirectory), ("json", Encode(project)));
        }
    }

    public WorkflowState[] List(Guid? projectId = null)
    {
        using var db = Open();
        return ReadMany<WorkflowState>(db, "SELECT state FROM workflows WHERE $id IS NULL OR project_id=$id ORDER BY id", projectId);
    }

    public WorkflowState? FindDuplicate(Guid projectId, string taskProviderId)
    {
        using var db = Open();
        using var query = Command(db, "SELECT w.state FROM workflows w JOIN tasks t ON w.task_id=t.id WHERE t.project_id=$project AND t.provider_id=$provider",
            null, ("project", projectId), ("provider", taskProviderId));
        return query.ExecuteScalar() is string value ? Decode<WorkflowState>(value) : null;
    }

    public WorkflowData? Find(Guid workflowId)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var state = ReadOne<WorkflowState>(db, "SELECT state FROM workflows WHERE id=$id", workflowId, transaction);
        if (state is null) return null;
        var snapshots = ReadMany<TaskSnapshot>(db, "SELECT json FROM snapshots WHERE workflow_id=$id ORDER BY rowid", workflowId, transaction);
        var operations = ReadMany<WorkflowOperation>(db, "SELECT json FROM operations WHERE workflow_id=$id ORDER BY rowid", workflowId, transaction);
        var passes = ReadMany<ManagedPass>(db, "SELECT json FROM passes WHERE workflow_id=$id ORDER BY rowid", workflowId, transaction);
        var sessions = ReadMany<AgentSession>(db, "SELECT json FROM sessions WHERE workflow_id=$id ORDER BY rowid", workflowId, transaction);
        var events = ReadMany<WorkflowEvent>(db, "SELECT json FROM events WHERE workflow_id=$id ORDER BY position", workflowId, transaction);
        var reports = ReadMany<ReviewReport>(db, "SELECT json FROM review_reports WHERE workflow_id=$id ORDER BY rowid", workflowId, transaction);
        using var query = Command(db, "SELECT pass_id,complete_json FROM results WHERE workflow_id=$id ORDER BY rowid", transaction, ("id", workflowId));
        using var reader = query.ExecuteReader();
        var results = new List<ValidatedResult>();
        while (reader.Read())
            results.Add(ResultEnvelopeContract.Parse(System.Text.Encoding.UTF8.GetBytes(reader.GetString(1)),
                passes.Single(p => p.Id == Guid.Parse(reader.GetString(0)))));
        return new(state, snapshots, operations, passes, sessions, [.. results], events, reports);
    }

    public void Create(ProjectRegistration project, string taskProviderId, WorkflowChange change)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            var state = change.State;
            Execute(db, "INSERT INTO tasks(id,project_id,provider_id) VALUES($id,$project,$provider)", transaction,
                ("id", state.TaskId), ("project", project.Id), ("provider", taskProviderId));
            Execute(db, "INSERT INTO workflows(id,project_id,task_id,scope,binding,intent,phase,version,state) VALUES($id,$project,$task,'WholeTask',$binding,$intent,$phase,0,$state)",
                transaction, ("id", state.Id), ("project", project.Id), ("task", state.TaskId), ("binding", Encode(state.Binding)),
                ("intent", Encode(state.WorkspaceIntent)), ("phase", state.Phase.ToString()), ("state", Encode(state)));
            InsertEvidence(db, transaction, change);
            transaction.Commit();
        }
    }

    public void Save(long expectedVersion, WorkflowChange change)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            var state = change.State;
            if (state.Version != expectedVersion + 1)
                throw new InvalidOperationException("A workflow transaction must advance its version once.");
            int updated = Execute(db, "UPDATE workflows SET phase=$phase,version=$next,state=$state WHERE id=$id AND version=$version AND binding=$binding AND intent=$intent",
                transaction, ("id", state.Id), ("version", expectedVersion), ("next", state.Version), ("state", Encode(state)),
                ("phase", state.Phase.ToString()), ("binding", Encode(state.Binding)), ("intent", Encode(state.WorkspaceIntent)));
            if (updated != 1) throw new WorkflowException("workflow_changed", "The workflow changed or its immutable binding was altered.", "Query the Workflow again before retrying.");
            if (change.Events.Length == 0) throw new InvalidOperationException("A mutation requires an audit event.");
            InsertEvidence(db, transaction, change);
            transaction.Commit();
        }
    }

    private static void InsertEvidence(SqliteConnection db, SqliteTransaction transaction, WorkflowChange change)
    {
        Guid workflow = change.State.Id;
        var frozen = ReadOne<PrIntent>(db, "SELECT intent FROM submissions WHERE workflow_id=$id", workflow, transaction);
        if (frozen is not null && Encode(frozen) != Encode(change.State.Submission?.Intent))
            throw new InvalidOperationException("A retained PR Intent is immutable.");
        if (change.State.Submission is { } submission)
        {
            if (submission.Intent.WorkflowId != workflow || submission.Intent.Binding != change.State.Binding ||
                submission.Intent.SnapshotId != change.State.ActiveSnapshotId)
                throw new InvalidOperationException("Submission must belong to this Workflow, binding and snapshot.");
            var previous = ReadOne<SubmissionState>(db, "SELECT json FROM submissions WHERE workflow_id=$id", workflow, transaction);
            if (submission.Commit is not null && submission.Commit != submission.Intent.Commit ||
                submission.PushedRevision is not null && (submission.PushedRevision != submission.Commit || submission.Commit is null) ||
                submission.PullRequest is not null && (submission.PushedRevision is null || !submission.ForgeWriteAttempted) ||
                previous?.Commit is not null && previous.Commit != submission.Commit ||
                previous?.PushedRevision is not null && previous.PushedRevision != submission.PushedRevision ||
                previous?.PullRequest is { } priorPr && (submission.PullRequest is null || priorPr.Id != submission.PullRequest.Id || priorPr.Number != submission.PullRequest.Number))
                throw new InvalidOperationException("Verified submission effects cannot be replaced, forgotten, or detached from the frozen commit.");
            Execute(db, """
                INSERT INTO submissions(workflow_id,intent_id,intent,commit_sha,pushed_sha,forge_attempted,pr_id,json)
                VALUES($workflow,$intent_id,$intent,$commit,$pushed,$attempted,$pr,$json)
                ON CONFLICT(workflow_id) DO UPDATE SET commit_sha=excluded.commit_sha,pushed_sha=excluded.pushed_sha,
                    forge_attempted=excluded.forge_attempted,pr_id=excluded.pr_id,json=excluded.json
                """, transaction, ("workflow", workflow), ("intent_id", submission.Intent.Id), ("intent", Encode(submission.Intent)),
                ("commit", submission.Commit), ("pushed", submission.PushedRevision), ("attempted", submission.ForgeWriteAttempted),
                ("pr", submission.PullRequest?.Id), ("json", Encode(submission)));
        }
        foreach (var snapshot in change.Snapshots)
            Execute(db, "INSERT INTO snapshots(id,workflow_id,json) VALUES($id,$workflow,$json)", transaction,
                ("id", snapshot.Id), ("workflow", workflow), ("json", Encode(snapshot)));
        foreach (var session in change.Sessions)
            Execute(db, "INSERT INTO sessions(id,workflow_id,json) VALUES($id,$workflow,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json WHERE sessions.workflow_id=excluded.workflow_id", transaction,
                ("id", session.Id), ("workflow", workflow), ("json", Encode(session)));
        foreach (var operation in change.Operations)
            Execute(db, """
                INSERT INTO operations(id,workflow_id,predecessor_id,kind,status,json) VALUES($id,$workflow,$predecessor,$kind,$status,$json)
                ON CONFLICT(id) DO UPDATE SET status=excluded.status,json=excluded.json
                WHERE operations.workflow_id=excluded.workflow_id AND operations.kind=excluded.kind
                    AND operations.predecessor_id IS excluded.predecessor_id
                """, transaction, ("id", operation.Id), ("workflow", workflow), ("predecessor", operation.PredecessorId),
                ("kind", operation.Kind), ("status", operation.Status.ToString()), ("json", Encode(operation)));
        foreach (var pass in change.Passes)
            Execute(db, "INSERT INTO passes(id,workflow_id,operation_id,snapshot_id,session_id,role,json) VALUES($id,$workflow,$operation,$snapshot,$session,$role,$json)", transaction,
                ("id", pass.Id), ("workflow", workflow), ("operation", pass.OperationId), ("snapshot", pass.SnapshotId),
                ("session", pass.SessionId), ("role", pass.Role.ToString()), ("json", Encode(pass)));
        foreach (var result in change.Results)
        {
            string outcome = result.Payload is ImplementationResult implementation ? implementation.Outcome : ((ReviewResult)result.Payload).Outcome;
            Execute(db, "INSERT INTO results(pass_id,workflow_id,operation_id,schema_version,role,outcome,summary,narrative,complete_json,payload_json) VALUES($pass,$workflow,$operation,$schema,$role,$outcome,$summary,$narrative,$complete,$payload)", transaction,
                ("pass", result.PassId), ("workflow", workflow), ("operation", result.OperationId), ("schema", result.SchemaVersion),
                ("role", result.Role.ToString()), ("outcome", outcome), ("summary", result.Payload.Report.Summary),
                ("narrative", result.Payload.Report.Narrative), ("complete", result.CompleteJson),
                ("payload", JsonSerializer.Serialize(result.Payload, result.Payload.GetType(), Json)));
            foreach (var check in result.Payload.Report.Checks)
                Execute(db, "INSERT INTO checks(workflow_id,pass_id,name,result,source,revision,json) VALUES($workflow,$pass,$name,$result,$source,$revision,$json)", transaction,
                    ("workflow", workflow), ("pass", result.PassId), ("name", check.Name), ("result", check.Result.ToString()),
                    ("source", check.Source), ("revision", check.Revision), ("json", Encode(check)));
        }
        foreach (var report in change.ReviewReports ?? [])
            Execute(db, "INSERT INTO review_reports(pass_id,workflow_id,json) VALUES($pass,$workflow,$json)", transaction,
                ("pass", report.PassId), ("workflow", workflow), ("json", Encode(report)));
        long position = Scalar<long>(db, "SELECT coalesce(max(position),0) FROM events WHERE workflow_id=$id", transaction, ("id", workflow));
        foreach (var item in change.Events)
        {
            if (item.SourceRetention is { } retention && Scalar<long>(db,
                "SELECT count(*) FROM snapshots WHERE id=$snapshot AND workflow_id=$workflow", transaction,
                ("snapshot", retention.SnapshotId), ("workflow", workflow)) != 1)
                throw new InvalidOperationException("Source retention must reference a snapshot owned by this Workflow.");
            var entry = item with { Position = ++position };
            Execute(db, "INSERT INTO events(workflow_id,position,kind,json) VALUES($workflow,$position,$kind,$json)", transaction,
                ("workflow", workflow), ("position", position), ("kind", entry.Kind), ("json", Encode(entry)));
        }
    }

    public void Delete(Guid workflowId, long expectedVersion)
    {
        lock (gate)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            string task = Scalar<string>(db, "SELECT task_id FROM workflows WHERE id=$id AND version=$version", transaction,
                ("id", workflowId), ("version", expectedVersion));
            Execute(db, "DELETE FROM workflows WHERE id=$id AND version=$version", transaction, ("id", workflowId), ("version", expectedVersion));
            Execute(db, "DELETE FROM tasks WHERE id=$id AND NOT EXISTS(SELECT 1 FROM workflows WHERE task_id=$id)", transaction, ("id", task));
            transaction.Commit();
        }
    }

    public void Backup(DateOnly day)
    {
        lock (gate)
            WithResource("current daily backup set", path + ".daily", () => { BackupCore(day); return true; });
    }

    private void BackupCore(DateOnly day)
    {
        SqliteMaintenance.RequireComplete(path);
        string directory = path + ".daily";
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".db");
        // Admit the complete current set, including older days, before retention can discard anything.
        foreach (string file in Directory.GetFiles(directory, "*.db")) VerifyFile(file);
        if (!File.Exists(destination)) CopyVerified(path, destination);
        foreach (string old in Directory.GetFiles(directory, "*.db").OrderDescending(StringComparer.Ordinal).Skip(7)) File.Delete(old);
    }

    public static string Reset(string path, bool force)
    {
        if (!force) throw new WorkflowException("force_required", "Database reset requires an explicit force choice.", "Use awctl database reset --force; clean retained workflows before a breaking upgrade.");
        return SqliteMaintenance.Replace(path, null);
    }

    public static string Restore(string path, string backup)
    {
        return SqliteMaintenance.Replace(path, backup);
    }

    internal static void CopyVerified(string source, string destination)
    {
        VerifyFile(source);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var db = Connect(source, SqliteOpenMode.ReadOnly))
            using (var backup = Connect(temporary, SqliteOpenMode.ReadWriteCreate)) db.BackupDatabase(backup);
            VerifyFile(temporary);
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private SqliteConnection Open()
    {
        try
        {
            var connection = WithResource("active database", path, () =>
            {
                SqliteMaintenance.RequireComplete(path);
                if (corrupt) throw Corruption(path);
                if (admitted) AdmitFile(path);
                return Connect(path, SqliteOpenMode.ReadWriteCreate);
            });
            Volatile.Write(ref admissionFailure, null);
            return connection;
        }
        catch (WorkflowException error)
        {
            if (error.Code == "database_corrupt") corrupt = true;
            Volatile.Write(ref admissionFailure, error);
            throw;
        }
    }

    private static SqliteConnection Connect(string file, SqliteOpenMode mode, int timeout = 5)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = file, Mode = mode, Pooling = false, ForeignKeys = true, DefaultTimeout = timeout }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    public static void VerifyFile(string path)
    {
        try
        {
            using var db = Connect(path, SqliteOpenMode.ReadOnly);
            VerifyConnection(db, path);
        }
        catch (SqliteException exception) { throw DatabaseError(path, exception); }
    }

    internal static long? ObserveSchema(string path)
    {
        try
        {
            using var db = Connect(path, SqliteOpenMode.ReadOnly, 1);
            return Scalar<long>(db, "PRAGMA user_version");
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException) { return null; }
    }

    internal static T WithResource<T>(string resource, string path, Func<T> action)
    {
        try { return action(); }
        catch (WorkflowException error) { throw new WorkflowException(error.Code, $"{resource} '{path}': {error.Message}", error.Guidance); }
        catch (SqliteException error) { throw DatabaseError(path, error); }
        catch (UnauthorizedAccessException) { throw new WorkflowException("database_access", $"Cannot access {resource} '{path}'.", "Restore directory/file permissions, then retry the same command. Preserved material must not be deleted."); }
        catch (IOException error) { throw new WorkflowException("database_unavailable", $"Cannot use {resource} '{path}': {error.Message}", "Preserve the files, restore storage access, then retry the same command."); }
    }

    internal static WorkflowException SchemaIncompatibility(string path, long version) =>
        new("schema_incompatible", $"Database '{path}' schema {version} differs from supported schema {SchemaVersion}.",
            "Use a compatible build, or explicitly run awctl database reset --force to archive the complete old store. No external reconciliation was performed.");

    private static void VerifyConnection(SqliteConnection db, string path)
    {
        using var transaction = db.BeginTransaction(deferred: true);
        long version = Scalar<long>(db, "PRAGMA user_version", transaction);
        if (version != SchemaVersion)
            throw SchemaIncompatibility(path, version);
        if (Scalar<string>(db, "PRAGMA integrity_check", transaction) != "ok") throw Corruption(path);
        using var query = Command(db, "PRAGMA foreign_key_check", transaction);
        using var reader = query.ExecuteReader();
        if (reader.Read()) throw Corruption(path);
        reader.Close();
        var actual = Structure(db, transaction);
        foreach (var (name, sql) in ExpectedStructure.Value)
            if (!actual.TryGetValue(name, out string? found) || found != sql)
                throw new WorkflowException("database_structure_invalid", $"Database '{path}' declares schema {version} but lacks supported AW structure '{name}'.",
                    "Preserve this file. Use an intact backup from a compatible build or explicitly archive and reset; changing user_version cannot convert a database.");
    }

    private static readonly Lazy<Dictionary<string, string>> ExpectedStructure = new(() =>
    {
        using var db = Connect(":memory:", SqliteOpenMode.ReadWriteCreate);
        Execute(db, Schema);
        return Structure(db, null);
    });

    private static Dictionary<string, string> Structure(SqliteConnection db, SqliteTransaction? transaction)
    {
        using var command = Command(db, "SELECT type || ':' || name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'", transaction);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    private static void AdmitFile(string path)
    {
        try { VerifyFile(path); }
        catch (WorkflowException exception) when (exception.Code == "database_recovery_required")
        {
            // A read-only reader cannot roll back a hot journal (SQLITE_READONLY_ROLLBACK).
            // Recover an isolated copy first: even an uncommitted user_version change must not
            // authorize recovery/mutation of an incompatible retained store. SQLite owns rollback.
            var temporary = Directory.CreateTempSubdirectory("aw-sqlite-admission-");
            try
            {
                if (OperatingSystem.IsLinux()) temporary.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                string candidate = Path.Combine(temporary.FullName, "store.db");
                var evidence = new Dictionary<string, byte[]?>();
                foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
                {
                    byte[]? identity = FileIdentity(path + suffix);
                    evidence.Add(suffix, identity);
                    if (identity is null) continue;
                    File.Copy(path + suffix, candidate + suffix);
                    if (!identity.AsSpan().SequenceEqual(FileIdentity(candidate + suffix))) throw RecoveryChanged();
                }
                using (var copy = Connect(candidate, SqliteOpenMode.ReadWrite)) VerifyConnection(copy, path);
                foreach (var (suffix, identity) in evidence)
                {
                    byte[]? current = FileIdentity(path + suffix);
                    if ((identity is null) != (current is null) || (identity is not null && !identity.AsSpan().SequenceEqual(current)))
                        throw RecoveryChanged();
                }
                // Native SQLite obtains the recovery locks and discards only the uncommitted transaction.
                using var recovered = Connect(path, SqliteOpenMode.ReadWrite);
                VerifyConnection(recovered, path);
            }
            catch (SqliteException error) { throw DatabaseError(path, error); }
            catch (UnauthorizedAccessException) { throw AccessError(); }
            catch (IOException) { throw new WorkflowException("database_unavailable", "Database recovery files could not be read consistently.", "Preserve the database and companions, restore local storage access, and retry startup."); }
            finally { temporary.Delete(true); }
        }
    }

    private static byte[]? FileIdentity(string file)
    {
        try { using var stream = File.OpenRead(file); return SHA256.HashData(stream); }
        catch (FileNotFoundException) { return null; }
    }
    private static WorkflowException RecoveryChanged() => new("database_busy", "Database files changed during crash-recovery admission.", "Allow the current database user to finish, then retry startup. No recovery was attempted on the retained store.");
    private static WorkflowException AccessError() => new("database_access", "The database or its companion files are not accessible with the required permissions.", "Restore access to the existing database directory and companion files, then retry. Do not reset or restore merely for an access failure.");
    private static WorkflowException DatabaseError(string path, SqliteException exception) => exception.SqliteErrorCode switch
    {
        11 or 26 => Corruption(path),
        5 or 6 => new("database_busy", $"Database '{path}' is locked by another connection.", "Allow the current database user to finish, then retry. The retained store has not been classified as corrupt."),
        8 when exception.SqliteExtendedErrorCode == 776 => new("database_recovery_required", "SQLite requires rollback of an interrupted transaction before the database can be read.", "Start the control plane with write access to the database and its directory so compatible-store admission can permit native SQLite recovery."),
        3 or 8 or 14 => new("database_access", $"Database '{path}' or its companions cannot be accessed.", AccessError().Guidance),
        _ => new("database_unavailable", $"SQLite could not verify '{path}' (error {exception.SqliteErrorCode}/{exception.SqliteExtendedErrorCode}).", "Preserve the database and companions, resolve the storage or connection failure, and retry. Corruption has not been established.")
    };

    private static WorkflowException Corruption(string path)
    {
        string directory = path + ".daily";
        if (Directory.Exists(directory))
            foreach (string backup in Directory.GetFiles(directory, "*.db").OrderDescending(StringComparer.Ordinal))
                try
                {
                    VerifyFile(backup);
                    return new("database_corrupt", $"Database '{path}' integrity could not be verified; ordinary mutation is blocked and the database is preserved.",
                        $"Explicitly restore the verified compatible backup: awctl database restore --backup '{backup}'. Restore archives the damaged database first.");
                }
                catch (WorkflowException) { }
        return new("database_corrupt", $"Database '{path}' integrity could not be verified; ordinary mutation is blocked and the database is preserved.",
            "No verified compatible daily backup was found. Preserve the damaged store and supply a known compatible backup to awctl database restore --backup PATH.");
    }
    private static string Encode<T>(T item) => JsonSerializer.Serialize(item, Json);
    private static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;
    private static T? ReadOne<T>(SqliteConnection db, string sql, object? id, SqliteTransaction? transaction = null)
    {
        using var query = Command(db, sql, transaction, ("id", id));
        return query.ExecuteScalar() is string json ? Decode<T>(json) : default;
    }
    private static T[] ReadMany<T>(SqliteConnection db, string sql, object? id, SqliteTransaction? transaction = null)
    {
        using var query = Command(db, sql, transaction, ("id", id));
        using var reader = query.ExecuteReader();
        var values = new List<T>();
        while (reader.Read()) values.Add(Decode<T>(reader.GetString(0)));
        return [.. values];
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string, object?)[] values)
    {
        var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var (name, value) in values) command.Parameters.AddWithValue("$" + name, value is Guid guid ? guid.ToString() : value ?? DBNull.Value);
        return command;
    }
    private static int Execute(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string, object?)[] values)
    { using var command = Command(db, sql, transaction, values); return command.ExecuteNonQuery(); }
    private static T Scalar<T>(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string, object?)[] values)
    { using var command = Command(db, sql, transaction, values); return (T)(command.ExecuteScalar() ?? throw new InvalidOperationException("Missing database record.")); }

    private const string Schema = """
        PRAGMA user_version=3;
        CREATE TABLE projects(id TEXT PRIMARY KEY, checkout TEXT NOT NULL UNIQUE, git_directory TEXT NOT NULL UNIQUE, json TEXT NOT NULL);
        CREATE TABLE tasks(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), provider_id TEXT NOT NULL, UNIQUE(project_id,provider_id), UNIQUE(id,project_id));
        CREATE TABLE workflows(id TEXT PRIMARY KEY, project_id TEXT NOT NULL, task_id TEXT NOT NULL, scope TEXT NOT NULL CHECK(scope='WholeTask'),
            binding TEXT NOT NULL,intent TEXT NOT NULL,phase TEXT NOT NULL CHECK(phase IN ('WorkspacePreparation','Implementation','Review','Submission')),version INTEGER NOT NULL,state TEXT NOT NULL,
            FOREIGN KEY(task_id,project_id) REFERENCES tasks(id,project_id),UNIQUE(task_id,scope));
        CREATE TABLE snapshots(id TEXT PRIMARY KEY,workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,json TEXT NOT NULL,UNIQUE(id,workflow_id));
        CREATE TABLE sessions(id TEXT PRIMARY KEY,workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,json TEXT NOT NULL,UNIQUE(id,workflow_id));
        CREATE TABLE operations(id TEXT PRIMARY KEY,workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,predecessor_id TEXT,kind TEXT NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('Pending','Running','Completed','Failed','Interrupted')),json TEXT NOT NULL,UNIQUE(id,workflow_id),
            FOREIGN KEY(predecessor_id,workflow_id) REFERENCES operations(id,workflow_id) DEFERRABLE INITIALLY DEFERRED);
        CREATE TABLE passes(id TEXT PRIMARY KEY,workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,operation_id TEXT NOT NULL UNIQUE,snapshot_id TEXT NOT NULL,session_id TEXT NOT NULL,
            role TEXT NOT NULL CHECK(role IN ('Implementer','Reviewer')),json TEXT NOT NULL,UNIQUE(id,workflow_id),
            FOREIGN KEY(operation_id,workflow_id) REFERENCES operations(id,workflow_id),FOREIGN KEY(snapshot_id,workflow_id) REFERENCES snapshots(id,workflow_id),FOREIGN KEY(session_id,workflow_id) REFERENCES sessions(id,workflow_id));
        CREATE TABLE results(pass_id TEXT PRIMARY KEY,workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,operation_id TEXT NOT NULL UNIQUE,
            schema_version INTEGER NOT NULL CHECK(schema_version=1),role TEXT NOT NULL,outcome TEXT NOT NULL,summary TEXT NOT NULL,narrative TEXT NOT NULL,
            complete_json TEXT NOT NULL CHECK(length(CAST(complete_json AS BLOB))<=1048576),payload_json TEXT NOT NULL,
            FOREIGN KEY(pass_id,workflow_id) REFERENCES passes(id,workflow_id),FOREIGN KEY(operation_id,workflow_id) REFERENCES operations(id,workflow_id));
        CREATE TABLE checks(workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,pass_id TEXT NOT NULL,name TEXT NOT NULL,
            result TEXT NOT NULL CHECK(result IN ('Passed','Failed','NotRun')),source TEXT NOT NULL,revision TEXT,json TEXT NOT NULL,
            FOREIGN KEY(pass_id,workflow_id) REFERENCES results(pass_id,workflow_id));
        CREATE UNIQUE INDEX results_owner ON results(pass_id,workflow_id);
        CREATE TABLE review_reports(pass_id TEXT PRIMARY KEY,workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,json TEXT NOT NULL,
            FOREIGN KEY(pass_id,workflow_id) REFERENCES results(pass_id,workflow_id));
        CREATE TABLE events(workflow_id TEXT NOT NULL REFERENCES workflows(id) ON DELETE CASCADE,position INTEGER NOT NULL CHECK(position>0),kind TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(workflow_id,position));
        CREATE TABLE submissions(workflow_id TEXT PRIMARY KEY REFERENCES workflows(id) ON DELETE CASCADE,intent_id TEXT NOT NULL UNIQUE,
            intent TEXT NOT NULL,commit_sha TEXT,pushed_sha TEXT,forge_attempted INTEGER NOT NULL,pr_id INTEGER,json TEXT NOT NULL);
        CREATE TRIGGER immutable_pr_intent BEFORE UPDATE OF intent,intent_id,workflow_id ON submissions BEGIN SELECT RAISE(ABORT,'PR intents are immutable'); END;
        CREATE TRIGGER retained_submission BEFORE DELETE ON submissions WHEN EXISTS(SELECT 1 FROM workflows WHERE id=OLD.workflow_id) BEGIN SELECT RAISE(ABORT,'Retained submission evidence is immutable'); END;
        CREATE TRIGGER immutable_snapshot BEFORE UPDATE ON snapshots BEGIN SELECT RAISE(ABORT,'Snapshots are immutable'); END;
        CREATE TRIGGER immutable_session BEFORE UPDATE ON sessions
            WHEN OLD.workflow_id<>NEW.workflow_id OR json_remove(OLD.json,'$.nativeResumeReference')<>json_remove(NEW.json,'$.nativeResumeReference')
                OR json_extract(OLD.json,'$.nativeResumeReference') IS NOT NULL
            BEGIN SELECT RAISE(ABORT,'Only initial native session observation may enrich a session'); END;
        CREATE TRIGGER immutable_pass BEFORE UPDATE ON passes BEGIN SELECT RAISE(ABORT,'Passes are immutable'); END;
        CREATE TRIGGER immutable_result BEFORE UPDATE ON results BEGIN SELECT RAISE(ABORT,'Results are immutable'); END;
        CREATE TRIGGER immutable_check BEFORE UPDATE ON checks BEGIN SELECT RAISE(ABORT,'Evidence is immutable'); END;
        CREATE TRIGGER immutable_review_report BEFORE UPDATE ON review_reports BEGIN SELECT RAISE(ABORT,'Review reports are immutable'); END;
        CREATE TRIGGER retained_review_report BEFORE DELETE ON review_reports WHEN EXISTS(SELECT 1 FROM workflows WHERE id=OLD.workflow_id) BEGIN SELECT RAISE(ABORT,'Retained review reports are immutable'); END;
        CREATE TRIGGER immutable_event BEFORE UPDATE ON events BEGIN SELECT RAISE(ABORT,'Events are append-only'); END;
        CREATE TRIGGER retained_event BEFORE DELETE ON events WHEN EXISTS(SELECT 1 FROM workflows WHERE id=OLD.workflow_id) BEGIN SELECT RAISE(ABORT,'Retained events are append-only'); END;
        CREATE TRIGGER retained_result BEFORE DELETE ON results WHEN EXISTS(SELECT 1 FROM workflows WHERE id=OLD.workflow_id) BEGIN SELECT RAISE(ABORT,'Retained results are immutable'); END;
        CREATE TRIGGER retained_snapshot BEFORE DELETE ON snapshots WHEN EXISTS(SELECT 1 FROM workflows WHERE id=OLD.workflow_id) BEGIN SELECT RAISE(ABORT,'Retained snapshots are immutable'); END;
        CREATE TRIGGER terminal_operation BEFORE UPDATE ON operations WHEN OLD.status IN ('Completed','Failed','Interrupted') BEGIN SELECT RAISE(ABORT,'Terminal operations are immutable'); END;
        CREATE TRIGGER immutable_binding BEFORE UPDATE OF binding,intent,task_id,project_id,scope ON workflows BEGIN SELECT RAISE(ABORT,'Workflow identity is immutable'); END;
        """;
}
