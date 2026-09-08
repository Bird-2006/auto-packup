namespace AutoPackup;

public sealed record BackupConfig(string SourceDirectory, string BackupDirectory, int IntervalMinutes);
public sealed record BackupConfigUpdate(string SourceDirectory, string BackupDirectory, int IntervalMinutes)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceDirectory)) return "Source directory is required.";
        if (string.IsNullOrWhiteSpace(BackupDirectory)) return "Backup directory is required.";
        if (IntervalMinutes is < 1 or > 1440) return "Interval must be between 1 and 1440 minutes.";
        return null;
    }
}
public sealed record BackupRun(long Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string Status, string SnapshotPath, long FileCount, long BytesCopied, long DurationMs, string? Error, string Kind = "Legacy", string? ShadowId = null, string? SourceVolume = null, string? DevicePath = null, string? SourceDirectory = null);
public sealed record BackupStatus(bool IsRunning, bool IsQueued, DateTimeOffset? StartedAt, DateTimeOffset? NextRunAt, string? LastError, string? Operation = null, long FilesProcessed = 0);
public sealed record RestoreRequest(string Destination, string? Path = null, bool ReplaceOriginal = false, bool ReplaceSource = false, bool DatabaseStopped = false);
public sealed record RestoreResult(bool Success, string Message, long FileCount = 0);
public sealed record FileEntry(string Name, string RelativePath, bool IsDirectory, long Size, DateTime LastWriteUtc);
public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message);
public sealed record ShadowInfo(string Id, string DevicePath, string Volume);
public sealed record ShadowStorage(string Volume, string StorageVolume, ulong UsedBytes, ulong AllocatedBytes, ulong? MaximumBytes, ulong FreeBytes);
public sealed record RuntimeMarker(bool CleanShutdown, long? RecoveryCandidateId, DateTimeOffset? RecoveryCandidateUntil);
public sealed record RecoveryInfo(BackupRun? Snapshot, bool PreviousShutdownWasClean, string Message);
