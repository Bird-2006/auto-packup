using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

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
        _logger?.Log(level == "Error" ? LogLevel.Error : LogLevel.Information, "{Message}", message);
        lock (_gate) { _entries.Add(new(DateTimeOffset.Now, level, message)); if (_entries.Count > 500) _entries.RemoveAt(0); }
    }
    public IReadOnlyList<LogEntry> Snapshot() { lock (_gate) return _entries.AsEnumerable().Reverse().ToArray(); }
}

public sealed class BackupWorker(BackupService service) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => service.RunLoopAsync(stoppingToken);
}

public sealed class BackupService(BackupRepository repo, IVolumeSnapshotProvider vss, LogBuffer logs, AppPaths paths, IArchiveCompressor compressor)
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _currentCancel;
    private bool _queued;
    private string? _operation;
    private DateTimeOffset? _started, _nextRun;
    private string? _lastError;
    private long _files;
    private int? _progress;
    private string? _activeArchive;
    private bool _previousShutdownWasClean = true;
    private long? _recoveryCandidateId;

    public BackupStatus GetStatus()
    {
        lock (_gate)
        {
            long? size = null;
            try { if (_activeArchive != null && File.Exists(_activeArchive)) size = new FileInfo(_activeArchive).Length; } catch (IOException) { }
            return new(_operation != null, _queued, _started, _nextRun, _lastError, _operation, Interlocked.Read(ref _files), _progress, size);
        }
    }

    public void SignalConfigurationChanged(int intervalMinutes)
    {
        lock (_gate) _nextRun = DateTimeOffset.UtcNow.AddMinutes(intervalMinutes);
        Wake();
    }
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    public bool TryQueueManualRun()
    {
        lock (_gate) { if (_operation != null || _queued) return false; _queued = true; }
        Wake(); return true;
    }
    public void CancelCurrentRun() { lock (_gate) { _queued = false; _currentCancel?.Cancel(); } }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        var previous = ReadMarker();
        _previousShutdownWasClean = previous.CleanShutdown;
        _recoveryCandidateId = previous.RecoveryCandidateId;
        WriteMarker(new RuntimeMarker(false, previous.RecoveryCandidateId, previous.RecoveryCandidateUntil));
        try { await ReconcileAsync(ct, startup: true); }
        catch (Exception ex) { logs.Error("Startup snapshot reconciliation failed", ex); lock (_gate) _lastError = ex.Message; }
        if (!previous.CleanShutdown)
        {
            var candidate = (await repo.ListRunsAsync(ct)).FirstOrDefault(x => x.Status == "Succeeded" && x.Kind != "Vss");
            _recoveryCandidateId = candidate?.Id;
            WriteMarker(new RuntimeMarker(false, _recoveryCandidateId, DateTimeOffset.UtcNow.AddHours(24)));
            lock (_gate) _queued = true;
            logs.Info("Previous shutdown was unclean; retaining the previous completed archive as the recovery candidate.");
        }
        try { await TrimAsync(ct); }
        catch (Exception ex) { logs.Error("Startup retention cleanup failed", ex); lock (_gate) _lastError = ex.Message; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var config = await repo.GetConfigAsync(ct);
                bool run;
                lock (_gate)
                {
                    _nextRun ??= DateTimeOffset.UtcNow.AddMinutes(config.IntervalMinutes);
                    run = _queued || DateTimeOffset.UtcNow >= _nextRun;
                }
                if (run) await RunOnceAsync(ct);
                else await ReconcileAsync(ct);
                TimeSpan delay;
                lock (_gate) delay = _queued ? TimeSpan.Zero : (_nextRun!.Value - DateTimeOffset.UtcNow);
                await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0.1, 30)), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logs.Error("Scheduler error", ex);
                lock (_gate) _lastError = ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        WriteMarker(new RuntimeMarker(true, null, null));
    }

    private RuntimeMarker ReadMarker()
    {
        try { return File.Exists(paths.RuntimeMarkerPath) ? JsonSerializer.Deserialize<RuntimeMarker>(File.ReadAllText(paths.RuntimeMarkerPath)) ?? new(true, null, null) : new(true, null, null); }
        catch { return new(false, null, null); }
    }
    private void WriteMarker(RuntimeMarker marker)
    {
        try { Directory.CreateDirectory(paths.DataDirectory); File.WriteAllText(paths.RuntimeMarkerPath, JsonSerializer.Serialize(marker)); }
        catch (Exception ex) { logs.Error("Could not update runtime marker", ex); }
    }

    public async Task<RecoveryInfo> GetRecoveryInfoAsync(CancellationToken ct)
    {
        var runs = await repo.ListRunsAsync(ct);
        var snapshot = (_recoveryCandidateId is long candidateId ? runs.FirstOrDefault(x => x.Id == candidateId && x.Status == "Succeeded") : null)
            ?? runs.FirstOrDefault(x => x.Status == "Succeeded" && x.Kind != "Vss");
        var message = snapshot == null ? "No usable VSS snapshot is available." : _previousShutdownWasClean ? $"Snapshot #{snapshot.Id} is the latest recovery point." : $"Snapshot #{snapshot.Id} is the recovery point from before the unexpected shutdown.";
        return new RecoveryInfo(snapshot, _previousShutdownWasClean, message);
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        var timer = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        long id = 0;
        string? archivePath = null;
        SnapshotHandle? shadow = null;
        bool committed = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) { _queued = false; _operation = "Snapshot"; _started = started; _lastError = null; _currentCancel = linked; _files = 0; _progress = null; }
        try
        {
            var config = await repo.GetConfigAsync(linked.Token);
            var source = PathRules.LocalDirectory(config.SourceDirectory);
            var backup = PathRules.LocalDirectory(config.BackupDirectory);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            if (PathRules.Contains(source, backup)) throw new IOException("Backup directory cannot be inside source.");
            Directory.CreateDirectory(backup);
            id = await repo.StartRunAsync(started, string.Empty, linked.Token);
            archivePath = Path.Combine(backup, $"snapshot-{started:yyyyMMdd-HHmmss}-{id}.zip");
            await repo.AttachShadowAsync(id, source, null, linked.Token);
            await repo.SetArchiveAsync(id, archivePath, null, linked.Token);
            // A temporary VSS is only the frozen input. The completed ZIP is independent of VSS.
            shadow = await vss.CreateAsync(source, linked.Token);
            await repo.AttachShadowAsync(id, source, shadow, CancellationToken.None);
            await repo.SetArchiveAsync(id, archivePath, null, CancellationToken.None);
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate) { _operation = "Compressing"; _progress = 0; _activeArchive = archivePath + ".partial"; }
            var threads = Math.Clamp(config.CompressionThreads, 1, Math.Min(Environment.ProcessorCount, 16));
            logs.Info($"ZIP backup {id}: {threads} threads, level {config.CompressionLevel}.");
            var stats = await compressor.CreateAsync(shadow.RootPath, archivePath + ".partial", threads, config.CompressionLevel, SetProgress, linked.Token, config.IncludeCrashDumps);
            Interlocked.Exchange(ref _files, stats.Files);
            lock (_gate) { _operation = "Verifying"; _progress = 0; }
            await compressor.VerifyAsync(archivePath + ".partial", threads, SetProgress, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            using (var file = new FileStream(archivePath + ".partial", FileMode.Open, FileAccess.ReadWrite)) file.Flush(true);
            var manifest = new ArchiveManifest(1, id, source, started, DateTimeOffset.UtcNow, timer.ElapsedMilliseconds, stats, config.IncludeCrashDumps);
            await File.WriteAllTextAsync(archivePath + ".json.partial", JsonSerializer.Serialize(manifest), linked.Token);
            using (var file = new FileStream(archivePath + ".json.partial", FileMode.Open, FileAccess.ReadWrite)) file.Flush(true);
            // A final manifest plus final ZIP allows startup recovery if the DB update is interrupted.
            File.Move(archivePath + ".json.partial", archivePath + ".json");
            File.Move(archivePath + ".partial", archivePath);
            committed = true;
            await repo.SetArchiveAsync(id, archivePath, stats.ArchiveBytes, CancellationToken.None);
            await repo.CompleteRunAsync(id, manifest.CompletedAt, "Succeeded", archivePath, stats.Files, stats.SourceBytes, manifest.DurationMs, null, CancellationToken.None);
            logs.Info($"ZIP backup {id} completed: {stats.Files} files, {stats.SourceBytes:N0} source bytes, {stats.ArchiveBytes:N0} archive bytes, {timer.Elapsed.TotalSeconds:F1} seconds (including verification).");
            try { await TrimAsync(CancellationToken.None); }
            catch (Exception ex) { logs.Error("Archive succeeded but retention cleanup failed", ex); lock (_gate) _lastError = ex.Message; }
        }
        catch (Exception ex)
        {
            logs.Error("ZIP backup failed", ex);
            lock (_gate) _lastError = ex is OperationCanceledException ? "Cancelled" : ex.Message;
            if (id != 0 && !committed)
                await repo.CompleteRunAsync(id, DateTimeOffset.UtcNow, "Failed", archivePath ?? string.Empty, _files, 0, timer.ElapsedMilliseconds, _lastError, CancellationToken.None);
        }
        finally
        {
            if (shadow != null)
            {
                try { await vss.DeleteAsync(shadow.ShadowId, CancellationToken.None); await repo.ClearShadowAsync(id, CancellationToken.None); }
                catch (Exception ex) { logs.Error("Temporary VSS cleanup requires retry", ex); }
            }
            if (archivePath != null && !committed) CleanupArchiveWork(archivePath);
            lock (_gate) { _operation = null; _started = null; _currentCancel = null; _progress = null; _activeArchive = null; }
            try
            {
                var config = await repo.GetConfigAsync(CancellationToken.None);
                lock (_gate) if (_nextRun == null || _nextRun <= DateTimeOffset.UtcNow) _nextRun = DateTimeOffset.UtcNow.AddMinutes(config.IntervalMinutes);
            }
            finally { _runLock.Release(); }
        }
    }

    private void SetProgress(int percent) { lock (_gate) _progress = percent; }

    private void CleanupArchiveWork(string archivePath)
    {
        if (!Path.GetFileName(archivePath).StartsWith("snapshot-", StringComparison.Ordinal) || !archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            foreach (var suffix in new[] { ".partial", ".json.partial" }) if (File.Exists(archivePath + suffix)) File.Delete(archivePath + suffix);
            if (!File.Exists(archivePath) && File.Exists(archivePath + ".json")) File.Delete(archivePath + ".json");
            var link = archivePath + ".partial.source";
            if (Directory.Exists(link) || new DirectoryInfo(link).LinkTarget != null) Directory.Delete(link);
        }
        catch (Exception ex) { logs.Error("Could not clean archive working files", ex); }
    }

    public async Task ReconcileAsync(CancellationToken ct, bool startup = false)
    {
        await _runLock.WaitAsync(ct);
        try { await ReconcileCoreAsync(ct, startup); }
        finally { _runLock.Release(); }
    }
    private async Task ReconcileCoreAsync(CancellationToken ct, bool startup)
    {
        Dictionary<string, ShadowInfo>? existing = null;
        try { existing = (await vss.ListAsync(ct)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logs.Error("VSS inventory unavailable; archive access is unaffected", ex); }
        foreach (var run in await repo.ListRunsAsync(ct))
        {
            if (run.Kind == "Zip")
            {
                if (run.ShadowId != null && (startup || run.Status != "Running") && existing != null)
                {
                    try { await vss.DeleteAsync(run.ShadowId, ct); await repo.ClearShadowAsync(run.Id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logs.Error("Temporary shadow cleanup failed", ex); }
                }
                if (startup && run.Status == "Running")
                {
                    ArchiveManifest? manifest = null;
                    if (File.Exists(run.SnapshotPath) && File.Exists(run.SnapshotPath + ".json"))
                    {
                        try { manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllTextAsync(run.SnapshotPath + ".json", ct)); }
                        catch (Exception ex) when (ex is JsonException or IOException) { logs.Error("Incomplete archive manifest", ex); }
                    }
                    if (manifest?.RunId == run.Id && manifest.Stats.ArchiveBytes == new FileInfo(run.SnapshotPath).Length)
                    {
                        await repo.SetArchiveAsync(run.Id, run.SnapshotPath, manifest.Stats.ArchiveBytes, ct);
                        await repo.CompleteRunAsync(run.Id, manifest.CompletedAt, "Succeeded", run.SnapshotPath, manifest.Stats.Files, manifest.Stats.SourceBytes, manifest.DurationMs, null, ct);
                    }
                    else
                    {
                        await repo.CompleteRunAsync(run.Id, DateTimeOffset.UtcNow, "Failed", run.SnapshotPath, 0, 0, 0, "Interrupted; previous completed archives were preserved.", ct);
                        CleanupArchiveWork(run.SnapshotPath);
                    }
                }
                else if (run.Status == "Succeeded" && !File.Exists(run.SnapshotPath))
                    await repo.SetStateAsync(run.Id, "Unavailable", "Archive file is missing.", ct);
                continue;
            }
            if (run.Kind == "Vss" && existing == null) continue;
            if (startup && run.Status == "Running")
            {
                if (run.Kind == "Vss" && run.ShadowId != null) await vss.DeleteAsync(run.ShadowId, ct);
                await repo.CompleteRunAsync(run.Id, DateTimeOffset.UtcNow, "Failed", run.SnapshotPath, 0, 0, 0, "Interrupted by service restart.", ct);
            }
            else if (run.Kind == "Vss" && run.Status == "Failed" && run.ShadowId != null && existing!.ContainsKey(run.ShadowId))
                await vss.DeleteAsync(run.ShadowId, ct);
            else if (run.Status == "Succeeded" || run.Kind == "Vss" && run.Status == "Unavailable")
            {
                var available = false;
                ShadowInfo? found = null;
                if (run.Kind == "Vss" && run.ShadowId != null && run.SourceDirectory != null && Path.GetPathRoot(run.SourceDirectory) != null && existing!.TryGetValue(run.ShadowId, out found))
                {
                    var suffix = run.DevicePath != null && run.SnapshotPath.StartsWith(run.DevicePath, StringComparison.OrdinalIgnoreCase)
                        ? run.SnapshotPath[run.DevicePath.Length..].TrimStart('\\')
                        : Path.GetRelativePath(Path.GetPathRoot(run.SourceDirectory)!, run.SourceDirectory);
                    var refreshed = Path.Combine(found.DevicePath + "\\", suffix);
                    available = Directory.Exists(refreshed);
                    if (!string.Equals(refreshed, run.SnapshotPath, StringComparison.OrdinalIgnoreCase)) await repo.UpdateShadowPathAsync(run.Id, found.DevicePath, refreshed, ct);
                }
                else if (run.Kind != "Vss") available = File.Exists(run.SnapshotPath) || Directory.Exists(run.SnapshotPath);
                if (!available) await repo.SetStateAsync(run.Id, "Unavailable", "Snapshot was removed or is inaccessible.", ct);
                else if (run.Status == "Unavailable") await repo.SetStateAsync(run.Id, "Succeeded", null, ct);
            }
        }
    }

    private async Task TrimAsync(CancellationToken ct)
    {
        var runs = (await repo.ListRunsAsync(ct)).Where(x => x.Kind != "Vss" && x.SnapshotPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && x.Status == "Succeeded").OrderByDescending(x => x.Id).ToList();
        var keep = runs.Take(3).Select(x => x.Id).ToHashSet();
        if (_recoveryCandidateId is long candidate && runs.Any(x => x.Id == candidate) && !keep.Contains(candidate))
        {
            if (keep.Count == 3) keep.Remove(keep.Min());
            keep.Add(candidate);
        }
        foreach (var old in runs.Where(x => !keep.Contains(x.Id))) await DeleteCoreAsync(old, ct);
    }
    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var run = await repo.GetRunAsync(id, ct);
            if (run == null || run.Status == "Running" || run.Status == "Deleted") return false;
            await DeleteCoreAsync(run, ct); return true;
        }
        finally { _runLock.Release(); }
    }
    private async Task DeleteCoreAsync(BackupRun run, CancellationToken ct)
    {
        if (run.Kind == "Vss")
        {
            if (run.ShadowId != null) await vss.DeleteAsync(run.ShadowId, ct);
        }
        else
        {
            // Only explicitly requested legacy artifacts with an application-generated name may be deleted.
            var path = PathRules.LocalDirectory(run.SnapshotPath);
            if (!Path.GetFileName(path).StartsWith("snapshot-", StringComparison.Ordinal)) throw new IOException("Unrecognized legacy snapshot path.");
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
            if (run.Kind == "Zip" && File.Exists(path + ".json")) File.Delete(path + ".json");
        }
        await repo.SetStateAsync(run.Id, "Deleted", null, ct);
        logs.Info($"Deleted snapshot {run.Id} ({run.Kind}).");
    }

    public async Task<RestoreResult> RestoreAsync(long id, string destination, string relative, CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct)) return new(false, "Another operation is active.");
        string? temp = null;
        var timer = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) { _operation = "Restore"; _started = DateTimeOffset.UtcNow; _currentCancel = linked; _files = 0; }
        try
        {
            await ReconcileCoreAsync(linked.Token, false);
            var run = await repo.GetRunAsync(id, linked.Token);
            if (run == null || run.Status != "Succeeded") return new(false, "Snapshot is unavailable.");
            var target = PathRules.LocalDirectory(destination);
            if (Directory.Exists(target) || File.Exists(target)) return new(false, "Restore requires a new destination directory.");
            var config = await repo.GetConfigAsync(linked.Token);
            if (PathRules.Contains(config.SourceDirectory, target) || PathRules.Contains(config.BackupDirectory, target) || (run.SourceDirectory != null && PathRules.Contains(run.SourceDirectory, target)))
                return new(false, "Restore destination cannot be inside source or backup directories.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            temp = target + ".restore-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(temp);
            if (run.Kind == "Vss" || Directory.Exists(run.SnapshotPath))
            {
                var root = run.Kind == "Vss" ? run.SnapshotPath : Path.Combine(run.SnapshotPath, "content");
                var source = PathRules.Resolve(root, relative);
                await CopyAsync(source, temp, linked.Token);
            }
            else
            {
                await ArchiveContent.ExtractAsync(run.SnapshotPath, temp, relative, () => Interlocked.Increment(ref _files), linked.Token);
            }
            linked.Token.ThrowIfCancellationRequested();
            Directory.Move(temp, target); temp = null;
            logs.Info($"Restored snapshot {id}: {_files} files in {timer.Elapsed.TotalSeconds:F1} seconds to {target}.");
            return new(true, $"Restored {_files} files in {timer.Elapsed.TotalSeconds:F1} seconds.", _files);
        }
        catch (Exception ex) { logs.Error("Restore failed", ex); return new(false, ex.Message, _files); }
        finally
        {
            if (temp != null) try { Directory.Delete(temp, true); } catch (Exception ex) { logs.Error("Restore temporary directory cleanup failed", ex); }
            lock (_gate) { _operation = null; _started = null; _currentCancel = null; }
            _runLock.Release();
        }
    }

    public async Task<RestoreResult> ReplaceOriginalAsync(long id, string relative, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relative)) return new(false, "Select a single database file to replace.");
        if (!await _runLock.WaitAsync(0, ct)) return new(false, "Another operation is active.");
        string? movedOriginal = null;
        var movedSidecars = new List<(string Current, string Backup)>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) { _operation = "Replace"; _started = DateTimeOffset.UtcNow; _currentCancel = linked; _files = 0; }
        try
        {
            await ReconcileCoreAsync(linked.Token, false);
            var run = await repo.GetRunAsync(id, linked.Token);
            if (run == null || run.Kind != "Vss" || run.Status != "Succeeded" || run.SourceDirectory == null) return new(false, "Use restore to a new directory or full restore for ZIP archives.");
            var sourceRoot = PathRules.Resolve(run.SnapshotPath, relative);
            if (!File.Exists(sourceRoot)) return new(false, "The selected snapshot item is not a file.");
            var target = PathRules.Resolve(run.SourceDirectory, relative);
            if (!File.Exists(target)) return new(false, "The original file no longer exists.");
            PathRules.RejectLinks(target);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            movedOriginal = target + $".before-recovery-{stamp}";
            File.Move(target, movedOriginal);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var sidecar = target + suffix;
                var snapshotSidecar = sourceRoot + suffix;
                if (File.Exists(sidecar))
                {
                    var sidecarBackup = sidecar + $".before-recovery-{stamp}";
                    File.Move(sidecar, sidecarBackup);
                    movedSidecars.Add((sidecar, sidecarBackup));
                }
                if (File.Exists(snapshotSidecar)) File.Copy(snapshotSidecar, sidecar, false);
            }
            try
            {
                File.Copy(sourceRoot, target, false);
            }
            catch
            {
                if (!File.Exists(target) && File.Exists(movedOriginal)) File.Move(movedOriginal, target);
                foreach (var (current, backup) in movedSidecars)
                    if (!File.Exists(current) && File.Exists(backup)) File.Move(backup, current);
                throw;
            }
            Interlocked.Increment(ref _files);
            logs.Info($"Replaced original file from snapshot {id}: {target}.");
            return new(true, $"Original file replaced. Previous file: {movedOriginal}", 1);
        }
        catch (Exception ex) { logs.Error("Original file replacement failed", ex); return new(false, ex.Message, _files); }
        finally
        {
            lock (_gate) { _operation = null; _started = null; _currentCancel = null; }
            _runLock.Release();
        }
    }

    public async Task<RestoreResult> ReplaceSourceAsync(long id, CancellationToken ct)
    {
        if (!await _runLock.WaitAsync(0, ct)) return new(false, "Another operation is active.");
        string? stage = null;
        string? movedSource = null;
        var timer = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) { _operation = "FullRestore"; _started = DateTimeOffset.UtcNow; _currentCancel = linked; _files = 0; }
        try
        {
            await ReconcileCoreAsync(linked.Token, false);
            var run = await repo.GetRunAsync(id, linked.Token);
            var config = await repo.GetConfigAsync(linked.Token);
            if (run == null || run.Status != "Succeeded") return new(false, "Backup is unavailable.");
            var source = PathRules.LocalDirectory(config.SourceDirectory);
            if (run.SourceDirectory != null && !string.Equals(source, PathRules.LocalDirectory(run.SourceDirectory), StringComparison.OrdinalIgnoreCase)) return new(false, "Snapshot source does not match the current configured source.");
            var isZip = run.SnapshotPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var snapshotRoot = run.Kind == "Vss" ? run.SnapshotPath : Path.Combine(run.SnapshotPath, "content");
            if (!(isZip ? File.Exists(run.SnapshotPath) : Directory.Exists(snapshotRoot))) return new(false, "Backup is inaccessible.");
            var parent = Directory.GetParent(source)?.FullName ?? throw new IOException("Source has no parent directory.");
            long sourceBytes;
            if (isZip) { using var zip = ZipFile.OpenRead(run.SnapshotPath); sourceBytes = zip.Entries.Sum(x => x.Length); }
            else sourceBytes = Directory.EnumerateFiles(snapshotRoot, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            var drive = new DriveInfo(Path.GetPathRoot(source)!);
            if (drive.AvailableFreeSpace < sourceBytes + 1024L * 1024 * 1024) throw new IOException($"Full restore needs about {sourceBytes / 1024 / 1024 / 1024.0:F1} GB free space, but only {drive.AvailableFreeSpace / 1024 / 1024 / 1024.0:F1} GB is available.");
            stage = Path.Combine(parent, ".autopackup-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            if (isZip) await ArchiveContent.ExtractAsync(run.SnapshotPath, stage, string.Empty, () => Interlocked.Increment(ref _files), linked.Token);
            else await CopyAsync(snapshotRoot, stage, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            movedSource = source + ".before-full-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.Move(source, movedSource);
            try { Directory.Move(stage, source); stage = null; }
            catch { if (!Directory.Exists(source) && Directory.Exists(movedSource)) Directory.Move(movedSource, source); throw; }
            logs.Info($"Full source restore completed from snapshot {id} in {timer.Elapsed.TotalSeconds:F1} seconds.");
            return new(true, $"Full restore completed in {timer.Elapsed.TotalSeconds:F1} seconds. Previous source: {movedSource}", _files);
        }
        catch (Exception ex) { logs.Error("Full source restore failed", ex); return new(false, ex.Message, _files); }
        finally
        {
            if (stage != null && Directory.Exists(stage)) try { Directory.Delete(stage, true); } catch (Exception ex) { logs.Error("Full restore staging cleanup failed", ex); }
            lock (_gate) { _operation = null; _started = null; _currentCancel = null; }
            _runLock.Release();
        }
    }

    private async Task CopyAsync(string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PathRules.RejectLinks(source);
        if (File.Exists(source))
        {
            var file = Path.Combine(target, Path.GetFileName(source));
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                await input.CopyToAsync(output, ct);
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(source));
            Interlocked.Increment(ref _files);
            return;
        }
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            PathRules.RejectLinks(entry);
            if (Directory.Exists(entry))
            {
                var child = Path.Combine(target, Path.GetFileName(entry)); Directory.CreateDirectory(child);
                await CopyAsync(entry, child, ct);
            }
            else await CopyAsync(entry, target, ct);
        }
    }
}
