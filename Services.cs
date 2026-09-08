using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace AutoPackup;

public sealed class LogBuffer
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();
    private ILogger? _logger;
    public void Attach(ILoggerFactory factory) => _logger = factory.CreateLogger("AutoPackup");
    public void Info(string message) => Add("Information", message);
    public void Error(string message, Exception? ex = null) => Add("Error", ex is null ? message : $"{message}: {ex.Message}");
    private void Add(string level, string message)
    {
        _logger?.Log(level == "Error" ? LogLevel.Error : LogLevel.Information, message);
        lock (_gate) { _entries.Add(new LogEntry(DateTimeOffset.Now, level, message)); if (_entries.Count > 500) _entries.RemoveRange(0, _entries.Count - 500); }
    }
    public IReadOnlyList<LogEntry> Snapshot() { lock (_gate) return _entries.OrderByDescending(x => x.Timestamp).ToArray(); }
}

public sealed record SnapshotHandle(string RootPath, string? ShadowId);
public interface IVolumeSnapshotProvider
{
    Task<SnapshotHandle> CreateAsync(string sourceDirectory, CancellationToken ct);
    Task DeleteAsync(SnapshotHandle snapshot, CancellationToken ct);
}

public sealed class WindowsVssSnapshotProvider : IVolumeSnapshotProvider
{
    private readonly LogBuffer _logs;
    public WindowsVssSnapshotProvider(LogBuffer logs) => _logs = logs;

    public async Task<SnapshotHandle> CreateAsync(string sourceDirectory, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VSS snapshots require Windows.");
        var root = Path.GetPathRoot(Path.GetFullPath(sourceDirectory));
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Source directory must be on a local volume.");
        var volume = root.TrimEnd('\\');
        var output = await RunAsync("vssadmin", $"create shadow /for={volume}", ct);
        var match = Regex.Match(output, @"(?i)(\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy\d+)");
        var idMatch = Regex.Match(output, @"(?i)\{[0-9a-f-]{36}\}");
        if (!match.Success || !idMatch.Success) throw new InvalidOperationException($"VSS did not return a shadow volume path or id. Output: {output}");
        var shadow = match.Groups[1].Value.TrimEnd('\\');
        var relative = Path.GetRelativePath(root, sourceDirectory);
        var path = Path.Combine(shadow, relative);
        _logs.Info($"Created VSS snapshot {shadow}.");
        return new SnapshotHandle(path, idMatch.Value);
    }

    public async Task DeleteAsync(SnapshotHandle snapshot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ShadowId)) return;
        try { await RunAsync("vssadmin", $"delete shadows /shadow={snapshot.ShadowId} /quiet", ct); _logs.Info("Deleted VSS snapshot."); }
        catch (Exception ex) { _logs.Error("Could not delete VSS snapshot", ex); }
    }

    private static async Task<string> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct); var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} failed ({process.ExitCode}): {stderr.Trim()} {stdout.Trim()}");
        return stdout + Environment.NewLine + stderr;
    }
}

public sealed class BackupWorker : BackgroundService
{
    private readonly BackupService _service;
    public BackupWorker(BackupService service) => _service = service;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) => await _service.RunLoopAsync(stoppingToken);
}

public sealed class BackupService
{
    private readonly BackupRepository _repo; private readonly IVolumeSnapshotProvider _vss; private readonly LogBuffer _logs;
    private readonly SemaphoreSlim _runLock = new(1, 1); private CancellationTokenSource? _currentCancel;
    private readonly object _gate = new(); private bool _queued; private bool _running; private DateTimeOffset? _started; private DateTimeOffset? _nextRun; private string? _lastError; private CancellationTokenSource _configChanged = new();
    public BackupService(BackupRepository repo, IVolumeSnapshotProvider vss, LogBuffer logs) { _repo = repo; _vss = vss; _logs = logs; }

    public BackupStatus GetStatus() { lock (_gate) return new BackupStatus(_running, _queued, _started, _nextRun, _lastError); }
    public void SignalConfigurationChanged() { lock (_gate) { _nextRun = null; _configChanged.Cancel(); _configChanged.Dispose(); _configChanged = new CancellationTokenSource(); } }
    public bool TryQueueManualRun() { lock (_gate) { if (_running || _queued) return false; _queued = true; } _ = Task.Run(() => RunOnceAsync(CancellationToken.None)); return true; }
    public void CancelCurrentRun() { try { _currentCancel?.Cancel(); } catch (ObjectDisposedException) { } }

    public async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = await _repo.GetConfigAsync(stoppingToken);
            DateTimeOffset due; lock (_gate) { due = _nextRun ?? DateTimeOffset.UtcNow; _nextRun = due; }
            var delay = due - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                CancellationToken configToken; lock (_gate) configToken = _configChanged.Token;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, configToken);
                try { await Task.Delay(delay, linked.Token); } catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { continue; }
            }
            await RunOnceAsync(stoppingToken);
            config = await _repo.GetConfigAsync(stoppingToken);
            lock (_gate) _nextRun = DateTimeOffset.UtcNow.AddMinutes(config.IntervalMinutes);
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        if (!await _runLock.WaitAsync(0, stoppingToken)) { lock (_gate) _queued = false; return; }
        var started = DateTimeOffset.UtcNow; var timer = Stopwatch.StartNew(); string? temp = null; SnapshotHandle? snapshot = null; long id = 0;
        lock (_gate) { _running = true; _queued = false; _started = started; _lastError = null; }
        _currentCancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        stoppingToken = _currentCancel.Token;
        try
        {
            var config = await _repo.GetConfigAsync(stoppingToken);
            if (!Directory.Exists(config.SourceDirectory)) throw new DirectoryNotFoundException(config.SourceDirectory);
            Directory.CreateDirectory(config.BackupDirectory);
            temp = Path.Combine(config.BackupDirectory, $".tmp-{started:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.zip");
            id = await _repo.StartRunAsync(started, temp, stoppingToken);
            snapshot = await _vss.CreateAsync(config.SourceDirectory, stoppingToken);
            await EnsureWorkingSpaceAsync(config.BackupDirectory, snapshot.RootPath, stoppingToken);
            var stats = await CreateZipAsync(snapshot.RootPath, temp, stoppingToken);
            var finalPath = Path.Combine(config.BackupDirectory, $"snapshot-{started:yyyyMMdd-HHmmss}.zip");
            File.Move(temp, finalPath); temp = null;
            await _repo.CompleteRunAsync(id, DateTimeOffset.UtcNow, "Succeeded", finalPath, stats.Files, stats.Bytes, timer.ElapsedMilliseconds, null, stoppingToken);
            await TrimAsync(config.BackupDirectory, stoppingToken); _logs.Info($"Backup completed: {stats.Files} files, {stats.Bytes:N0} bytes.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { if (id != 0) await _repo.CompleteRunAsync(id, DateTimeOffset.UtcNow, "Failed", temp ?? string.Empty, 0, 0, timer.ElapsedMilliseconds, "Cancelled", CancellationToken.None); }
        catch (Exception ex)
        {
            lock (_gate) _lastError = ex.Message; _logs.Error("Backup failed", ex);
            if (id != 0) await _repo.CompleteRunAsync(id, DateTimeOffset.UtcNow, "Failed", temp ?? string.Empty, 0, 0, timer.ElapsedMilliseconds, ex.Message, CancellationToken.None);
        }
        finally
        {
            if (snapshot is not null) await _vss.DeleteAsync(snapshot, CancellationToken.None);
            if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch { }
            lock (_gate) { _running = false; _started = null; }
            _currentCancel?.Dispose(); _currentCancel = null;
            _runLock.Release();
        }
    }

    public async Task<RestoreResult> RestoreAsync(long id, string destination, CancellationToken ct)
    {
        var run = await _repo.GetRunAsync(id, ct); if (run is null || run.Status != "Succeeded" || (!Directory.Exists(run.SnapshotPath) && !File.Exists(run.SnapshotPath))) return new(false, "Snapshot not found.");
        var target = Path.GetFullPath(destination); Directory.CreateDirectory(target);
        if (File.Exists(run.SnapshotPath) && run.SnapshotPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var stats = await ExtractZipAsync(run.SnapshotPath, target, ct); _logs.Info($"Restored compressed snapshot {id} to {target}."); return new(true, "Restore completed.", stats.Files);
        }
        var source = Path.Combine(run.SnapshotPath, "content"); var directoryStats = await CopyTreeAsync(source, target, ct); _logs.Info($"Restored snapshot {id} to {target}."); return new(true, "Restore completed.", directoryStats.Files);
    }

    private static async Task<(long Files, long Bytes)> CopyTreeAsync(string source, string destination, CancellationToken ct)
    {
        long files = 0, bytes = 0; Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { ct.ThrowIfCancellationRequested(); var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); await using var input = File.OpenRead(file); await using var output = File.Create(target); await input.CopyToAsync(output, ct); var info = new FileInfo(file); files++; bytes += info.Length; }
        return (files, bytes);
    }

    private static async Task<(long Files, long Bytes)> CreateZipAsync(string source, string archivePath, CancellationToken ct)
    {
        long files = 0, bytes = 0;
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var entry = archive.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Fastest);
            entry.LastWriteTime = info.LastWriteTimeUtc;
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var entryStream = entry.Open();
            await input.CopyToAsync(entryStream, ct);
            files++; bytes += info.Length;
        }
        return (files, bytes);
    }

    private static async Task<(long Files, long Bytes)> ExtractZipAsync(string archivePath, string destination, CancellationToken ct)
    {
        long files = 0, bytes = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(destination);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open(); await using var output = File.Create(target); await input.CopyToAsync(output, ct); files++; bytes += entry.Length;
        }
        return (files, bytes);
    }

    private async Task EnsureWorkingSpaceAsync(string backupDirectory, string sourcePath, CancellationToken ct)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(backupDirectory))!);
        var sourceBytes = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
        var required = sourceBytes + 1024L * 1024 * 1024;
        var runs = (await _repo.ListRunsAsync(ct)).Where(x => x.Status == "Succeeded" && x.SnapshotPath.StartsWith(Path.GetFullPath(backupDirectory), StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.CompletedAt).ToList();
        while (drive.AvailableFreeSpace < required && runs.Count > 0)
        {
            var oldest = runs[0]; runs.RemoveAt(0);
            await _repo.DeleteRunAsync(oldest.Id, ct);
            drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(backupDirectory))!);
            _logs.Info($"Deleted oldest snapshot before compression to make room: {oldest.SnapshotPath}");
        }
        if (drive.AvailableFreeSpace < required) throw new IOException($"备份磁盘可用空间不足。至少需要约 {required / 1024 / 1024 / 1024.0:F1} GB，当前仅剩 {drive.AvailableFreeSpace / 1024 / 1024 / 1024.0:F1} GB。请更换备份目录或清理磁盘。");
    }
    private async Task TrimAsync(string backupDirectory, CancellationToken ct)
    {
        var runs = (await _repo.ListRunsAsync(ct)).Where(x => x.Status == "Succeeded" && x.SnapshotPath.StartsWith(Path.GetFullPath(backupDirectory), StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.CompletedAt).ToList();
        foreach (var old in runs.Skip(3)) { if (Directory.Exists(old.SnapshotPath)) Directory.Delete(old.SnapshotPath, true); else if (File.Exists(old.SnapshotPath)) File.Delete(old.SnapshotPath); }
    }
}
