using System.Diagnostics;
using AgenticWorkflows.Infrastructure;
using AgenticWorkflows.Workflows;
using Microsoft.Data.Sqlite;

namespace AgenticWorkflows.Tests;

public sealed class SqliteRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Killed_transaction_recovers_only_a_compatible_committed_schema(bool compatible)
    {
        string root = Directory.CreateTempSubdirectory("aw-sqlite-crash-test-").FullName;
        try
        {
            string path = Path.Combine(root, "state.db");
            _ = new SqliteWorkflowStore(path);
            int committedVersion = compatible ? SqliteWorkflowStore.SchemaVersion : 999;
            using (var db = Connect(path))
            {
                Execute(db, $"PRAGMA user_version={committedVersion}; CREATE TABLE crash_probe(payload TEXT NOT NULL); INSERT INTO crash_probe VALUES('committed evidence');");
            }
            byte[] before = await File.ReadAllBytesAsync(path);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(SqliteRecoveryTests).Assembly.Location);
            start.ArgumentList.Add("sqlite-transaction-crash"); start.ArgumentList.Add(path);
            start.ArgumentList.Add((compatible ? 999 : SqliteWorkflowStore.SchemaVersion).ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (var child = Process.Start(start)!)
            {
                try
                {
                    Assert.Equal("transaction-spilled", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
                    Assert.True(File.Exists(path + "-journal"));
                    child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.NotEqual(0, child.ExitCode);
                    Assert.Empty(await child.StandardError.ReadToEndAsync());
                }
                finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            }
            byte[] interrupted = await File.ReadAllBytesAsync(path);
            byte[] journal = await File.ReadAllBytesAsync(path + "-journal");
            Assert.False(before.SequenceEqual(interrupted)); // Data pages really reached disk before process death.
            Assert.True(journal.Length > 512);
            Assert.Equal("database_recovery_required", Assert.Throws<WorkflowException>(() => SqliteWorkflowStore.VerifyFile(path)).Code);
            if (compatible)
            {
                var restarted = new SqliteWorkflowStore(path);
                Assert.Empty(restarted.List());
                using var db = Connect(path);
                using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM crash_probe";
                Assert.Equal("committed evidence", command.ExecuteScalar());
                command.CommandText = "PRAGMA user_version"; Assert.Equal((long)SqliteWorkflowStore.SchemaVersion, command.ExecuteScalar());
                Assert.False(File.Exists(path + "-journal"));
                SqliteWorkflowStore.VerifyFile(path);
                restarted.Backup(new DateOnly(2026, 10, 24));
                SqliteWorkflowStore.VerifyFile(Directory.GetFiles(path + ".daily").Single());
            }
            else
            {
                Assert.Equal("schema_incompatible", Assert.Throws<WorkflowException>(() => new SqliteWorkflowStore(path)).Code);
                Assert.Equal(interrupted, await File.ReadAllBytesAsync(path));
                Assert.Equal(journal, await File.ReadAllBytesAsync(path + "-journal"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_database_is_an_access_failure_not_corruption()
    {
        string path = Path.Combine(Path.GetTempPath(), "aw-missing-" + Guid.NewGuid().ToString("N"), "state.db");
        Assert.Equal("database_access", Assert.Throws<WorkflowException>(() => SqliteWorkflowStore.VerifyFile(path)).Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Exclusive_lock_does_not_poison_the_store_as_corrupt()
    {
        string root = Directory.CreateTempSubdirectory("aw-sqlite-lock-test-").FullName;
        try
        {
            string path = Path.Combine(root, "state.db");
            var store = new SqliteWorkflowStore(path);
            using var blocker = Connect(path);
            Execute(blocker, "BEGIN EXCLUSIVE;");
            try { Assert.Equal("database_busy", Assert.Throws<WorkflowException>(() => store.List()).Code); }
            finally { Execute(blocker, "ROLLBACK;"); }
            Assert.Empty(store.List());
            Assert.Empty(new SqliteWorkflowStore(path).List());
        }
        finally { Directory.Delete(root, true); }
    }

    private static SqliteConnection Connect(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static void Execute(SqliteConnection db, string sql)
    { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
}
