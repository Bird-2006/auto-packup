namespace AutoPackup;

public sealed record BackupConfig(string SourceDirectory, string BackupDirectory, int IntervalMinutes, int CompressionThreads = 4, int CompressionLevel = 1, bool IncludeCrashDumps = true);
public sealed record BackupConfigUpdate(string SourceDirectory, string BackupDirectory, int IntervalMinutes, int CompressionThreads = 4, int CompressionLevel = 1, bool IncludeCrashDumps = true)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory)) return "Source directory is required.";
        if (string.IsNullOrWhiteSpace(BackupDirectory)) return "Backup directory is required.";
        if (IntervalMinutes is < 1 or > 1440) return "Interval must be between 1 and 1440 minutes.";
        if (CompressionThreads is < 1 or > 16) return "Compression threads must be between 1 and 16.";
        if (CompressionLevel is not (1 or 3)) return "Compression level must be 1 (fast) or 3 (balanced).";
        return null;
    }
}
public sealed record BackupRun(long Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string Status, string SnapshotPath, long FileCount, long BytesCopied, long DurationMs, string? Error, string Kind = "Legacy", string? ShadowId = null, string? SourceVolume = null, string? DevicePath = null, string? SourceDirectory = null, long? ArchiveBytes = null);
public sealed record BackupStatus(bool IsRunning, bool IsQueued, DateTimeOffset? StartedAt, DateTimeOffset? NextRunAt, string? LastError, string? Operation = null, long FilesProcessed = 0, int? ProgressPercent = null, long? ArchiveBytes = null);
public sealed record RestoreRequest(string Destination, string? Path = null, bool ReplaceOriginal = false, bool ReplaceSource = false, bool DatabaseStopped = false);
public sealed record RestoreResult(bool Success, string Message, long FileCount = 0);
public sealed record FileEntry(string Name, string RelativePath, bool IsDirectory, long Size, DateTime LastWriteUtc);
public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message);
public sealed record ShadowInfo(string Id, string DevicePath, string Volume);
public sealed record ShadowStorage(string Volume, string StorageVolume, ulong UsedBytes, ulong AllocatedBytes, ulong? MaximumBytes, ulong FreeBytes);
public sealed record RuntimeMarker(bool CleanShutdown, long? RecoveryCandidateId, DateTimeOffset? RecoveryCandidateUntil);
public sealed record RecoveryInfo(BackupRun? Snapshot, bool PreviousShutdownWasClean, string Message);
