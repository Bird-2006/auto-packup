using Microsoft.Data.Sqlite;
using System.IO.Compression;

namespace AutoPackup;

public sealed class BackupRepository
{
    private readonly string _connectionString;
    private readonly AppPaths _paths;

    public BackupRepository(AppPaths paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS config (id INTEGER PRIMARY KEY CHECK (id=1), source_directory TEXT NOT NULL, backup_directory TEXT NOT NULL, interval_minutes INTEGER NOT NULL);
            INSERT OR IGNORE INTO config (id, source_directory, backup_directory, interval_minutes) VALUES (1, 'D:\BaiduNetdiskDownload', $backup, 30);
            CREATE TABLE IF NOT EXISTS backup_runs (id INTEGER PRIMARY KEY AUTOINCREMENT, started_at TEXT NOT NULL, completed_at TEXT, status TEXT NOT NULL, snapshot_path TEXT NOT NULL, file_count INTEGER NOT NULL DEFAULT 0, bytes_copied INTEGER NOT NULL DEFAULT 0, error TEXT);
            """;
        command.Parameters.AddWithValue("$backup", _paths.BackupDirectory);
        command.ExecuteNonQuery();
        try
        {
            using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE backup_runs ADD COLUMN duration_ms INTEGER NOT NULL DEFAULT 0";
            migration.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
        var columns = new HashSet<string>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(backup_runs)";
            using var reader = schema.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        foreach (var (name, definition) in new[] { ("kind", "TEXT NOT NULL DEFAULT 'Legacy'"), ("shadow_id", "TEXT"), ("source_volume", "TEXT"), ("device_path", "TEXT"), ("source_directory", "TEXT") })
        {
            if (columns.Contains(name)) continue;
            using var migration = connection.CreateCommand();
            migration.CommandText = $"ALTER TABLE backup_runs ADD COLUMN {name} {definition}";
            migration.ExecuteNonQuery();
        }
    }

    public async Task<BackupConfig> GetConfigAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_directory, backup_directory, interval_minutes FROM config WHERE id=1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new BackupConfig(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
    }

    public async Task<BackupConfig> UpdateConfigAsync(BackupConfigUpdate update, CancellationToken ct)
    {
        PathRules.LocalDirectory(update.SourceDirectory);
        PathRules.LocalDirectory(update.BackupDirectory);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE config SET source_directory=$source, backup_directory=$backup, interval_minutes=$interval WHERE id=1";
        command.Parameters.AddWithValue("$source", Path.GetFullPath(update.SourceDirectory));
        command.Parameters.AddWithValue("$backup", Path.GetFullPath(update.BackupDirectory));
        command.Parameters.AddWithValue("$interval", update.IntervalMinutes);
        await command.ExecuteNonQueryAsync(ct);
        Directory.CreateDirectory(Path.GetFullPath(update.BackupDirectory));
        return await GetConfigAsync(ct);
    }

    public async Task<long> StartRunAsync(DateTimeOffset started, string tempPath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO backup_runs (started_at,status,snapshot_path) VALUES ($started,'Running',$path); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$started", started.ToString("O")); command.Parameters.AddWithValue("$path", tempPath);
        return (long)(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task CompleteRunAsync(long id, DateTimeOffset completed, string status, string path, long files, long bytes, long durationMs, string? error, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_runs SET completed_at=$completed,status=$status,snapshot_path=$path,file_count=$files,bytes_copied=$bytes,duration_ms=$duration,error=$error WHERE id=$id";
        command.Parameters.AddWithValue("$completed", completed.ToString("O")); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$path", path); command.Parameters.AddWithValue("$files", files); command.Parameters.AddWithValue("$bytes", bytes); command.Parameters.AddWithValue("$duration", durationMs); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value); command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<BackupRun>> ListRunsAsync(CancellationToken ct)
    {
        var result = new List<BackupRun>();
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT id,started_at,completed_at,status,snapshot_path,file_count,bytes_copied,duration_ms,error,kind,shadow_id,source_volume,device_path,source_directory FROM backup_runs ORDER BY id DESC";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new BackupRun(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
        return result;
    }

    public async Task<BackupRun?> GetRunAsync(long id, CancellationToken ct)
    {
        return (await ListRunsAsync(ct)).FirstOrDefault(x => x.Id == id);
    }

    public async Task AttachShadowAsync(long id, string source, SnapshotHandle? shadow, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_runs SET kind='Vss',source_directory=$source,shadow_id=$shadow,source_volume=$volume,device_path=$device,snapshot_path=$path WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$shadow", (object?)shadow?.ShadowId ?? DBNull.Value);
        command.Parameters.AddWithValue("$volume", (object?)shadow?.Volume ?? DBNull.Value);
        command.Parameters.AddWithValue("$device", (object?)shadow?.DevicePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", shadow?.RootPath ?? string.Empty);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetStateAsync(long id, string status, string? error, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_runs SET status=$status,error=$error WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FileEntry>?> ListFilesAsync(long id, string relativePath, CancellationToken ct)
    {
        var run = await GetRunAsync(id, ct); if (run is null || run.Status != "Succeeded" || (!Directory.Exists(run.SnapshotPath) && !File.Exists(run.SnapshotPath))) return null;
        if (File.Exists(run.SnapshotPath) && run.SnapshotPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return await ListZipFilesAsync(run.SnapshotPath, relativePath, ct);
        }
        var root = Path.GetFullPath(run.Kind == "Vss" ? run.SnapshotPath : Path.Combine(run.SnapshotPath, "content")); var current = PathRules.Resolve(root, relativePath);
        if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var entries = new List<FileEntry>();
        foreach (var dir in Directory.EnumerateDirectories(current)) entries.Add(new FileEntry(Path.GetFileName(dir), Path.GetRelativePath(root, dir), true, 0, Directory.GetLastWriteTimeUtc(dir)));
        foreach (var file in Directory.EnumerateFiles(current)) { var info = new FileInfo(file); entries.Add(new FileEntry(info.Name, Path.GetRelativePath(root, file), false, info.Length, info.LastWriteTimeUtc)); }
        return entries;
    }

    private static Task<IReadOnlyList<FileEntry>?> ListZipFilesAsync(string archivePath, string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var prefix = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (prefix.Length > 0) prefix += "/";
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new List<FileEntry>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var remainder = name[prefix.Length..];
            if (remainder.Length == 0) continue;
            var slash = remainder.IndexOf('/');
            if (slash >= 0)
            {
                var directory = remainder[..slash];
                if (directories.Add(directory)) entries.Add(new FileEntry(directory, CombineRelative(prefix, directory), true, 0, entry.LastWriteTime.UtcDateTime));
            }
            else if (!name.EndsWith('/'))
            {
                entries.Add(new FileEntry(remainder, CombineRelative(prefix, remainder), false, entry.Length, entry.LastWriteTime.UtcDateTime));
            }
        }
        return Task.FromResult<IReadOnlyList<FileEntry>?>(entries);
    }

    private static string CombineRelative(string prefix, string value) => (prefix + value).Trim('/');
}
