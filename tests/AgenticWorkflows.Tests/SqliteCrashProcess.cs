using Microsoft.Data.Sqlite;

namespace AgenticWorkflows.Tests;

// Test-only child entry point. The parent kills this process after SQLite spills an open
// transaction to disk; disposal must never roll back the transaction before that kill.
internal static class SqliteCrashProcess
{
    public static int Main(string[] args)
    {
        if (args is not ["sqlite-transaction-crash", var path, var version] || !int.TryParse(version, out int schema)) return 2;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA cache_size=4; PRAGMA cache_spill=ON;";
        command.ExecuteNonQuery();
        using var transaction = db.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version={schema}; UPDATE crash_probe SET payload=hex(randomblob(262144));";
        command.ExecuteNonQuery();
        Console.WriteLine("transaction-spilled");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 3;
    }
}
