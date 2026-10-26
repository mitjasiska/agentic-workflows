using AgenticWorkflows.Workflows;

namespace AgenticWorkflows.Infrastructure;

/// <summary>Stage a complete replacement before moving anything; resume only explicit maintenance.</summary>
internal static class SqliteMaintenance
{
    internal static void RequireComplete(string path)
    {
        if (Directory.Exists(path + ".maintenance"))
            throw new WorkflowException("database_maintenance", $"Database maintenance at '{path}.maintenance' is incomplete; ordinary admission is blocked.",
                "Run awctl database status, then repeat the pending reset --force or restore --backup PATH command. Restart after maintenance completes. No manual file edits are needed.");
    }

    internal static string Replace(string path, string? backup, Action<string>? checkpoint = null)
    {
        path = Path.GetFullPath(path);
        return SqliteWorkflowStore.WithResource("database maintenance", path, () => ReplaceCore(path, backup, checkpoint));
    }

    private static string ReplaceCore(string path, string? backup, Action<string>? checkpoint)
    {
        backup = backup is null ? null : Path.GetFullPath(backup);
        string pending = path + ".maintenance", action = backup is null ? "reset" : "restore";
        string name = Path.GetFileName(path);
        if (!Directory.Exists(pending))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string staging = path + ".prepare-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                string replacement = Path.Combine(staging, name);
                if (backup is null) _ = new SqliteWorkflowStore(replacement);
                else SqliteWorkflowStore.WithResource("selected restore backup", backup, () =>
                { SqliteWorkflowStore.CopyVerified(backup, replacement); return true; });
                new SqliteWorkflowStore(replacement).Backup(DateOnly.FromDateTime(DateTime.UtcNow));
                WriteDurable(Path.Combine(staging, "action"), action);
                if (backup is not null) WriteDurable(Path.Combine(staging, "backup"), backup);
                WriteDurable(Path.Combine(staging, "archive"), name + ".archive-" +
                    DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                Directory.Move(staging, pending);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            checkpoint?.Invoke("prepared");
        }

        string pendingAction = File.ReadAllText(Path.Combine(pending, "action"));
        string? pendingBackup = pendingAction == "restore" ? File.ReadAllText(Path.Combine(pending, "backup")) : null;
        string continuation = pendingBackup is null ? "awctl database reset --force" :
            "awctl database restore --backup '" + pendingBackup.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        if (pendingAction != action)
            throw new WorkflowException("database_maintenance", $"A pending {pendingAction} for '{path}' must finish before {action}.",
                $"Repeat {continuation}; the verified replacement is already staged, so the original source path is not read again.");
        if (backup != pendingBackup)
            throw new WorkflowException("database_maintenance", $"Selected restore backup '{backup}' differs from pending selection '{pendingBackup}' for '{path}'. No maintenance files were changed.",
                $"Repeat {continuation} to finish the pending restore, then restart. The original selection's verified copy remains staged even if its source has moved into the archive.");
        string archiveName = File.ReadAllText(Path.Combine(pending, "archive"));
        if (archiveName != Path.GetFileName(archiveName) || !archiveName.StartsWith(name + ".archive-", StringComparison.Ordinal))
            throw new WorkflowException("database_maintenance", $"Invalid maintenance archive path in '{pending}'.", "Preserve the maintenance directory and report this storage failure.");
        string archive = Path.Combine(Path.GetDirectoryName(path)!, archiveName);
        string archived = Path.Combine(pending, "archived");
        if (!File.Exists(archived))
        {
            // No replacement has been installed until every old member is safely archived.
            SqliteWorkflowStore.VerifyFile(Path.Combine(pending, name));
            Directory.CreateDirectory(archive);
            foreach (string suffix in new[] { "", "-wal", "-shm", "-journal", ".daily" })
            {
                MoveIfPresent(path + suffix, Path.Combine(archive, name + suffix));
                checkpoint?.Invoke("archive" + suffix);
            }
            WriteDurable(archived, "complete");
            checkpoint?.Invoke("archived");
        }
        Install(Path.Combine(pending, name), path);
        checkpoint?.Invoke("installed-database");
        Install(Path.Combine(pending, name + ".daily"), path + ".daily");
        checkpoint?.Invoke("installed-backups");
        SqliteWorkflowStore.VerifyFile(path);
        foreach (string file in Directory.GetFiles(path + ".daily", "*.db")) SqliteWorkflowStore.VerifyFile(file);
        // Atomic completion: never partially delete a checkpoint needed to resume.
        Directory.Move(pending, Path.Combine(archive, "maintenance"));
        return archive;
    }

    private static void Install(string source, string destination)
    {
        if (File.Exists(source) || Directory.Exists(source)) MoveIfPresent(source, destination);
        else if (!File.Exists(destination) && !Directory.Exists(destination)) throw new IOException("Missing staged or installed replacement: " + destination);
    }

    private static void MoveIfPresent(string source, string destination)
    {
        if (File.Exists(source)) File.Move(source, destination);
        else if (Directory.Exists(source)) Directory.Move(source, destination);
    }

    private static void WriteDurable(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(value));
        stream.Flush(flushToDisk: true);
    }
}
