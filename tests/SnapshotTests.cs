using AutoPackup;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

public sealed class SnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "autopackup-test-" + Guid.NewGuid().ToString("N"));
    private readonly BackupRepository repo;
    private readonly FakeVss vss;
    private readonly TestCompressor compressor;
    private readonly BackupService service;
    private string Source => Path.Combine(root, "source");
    public SnapshotTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "live.txt"), "before");
        Directory.CreateDirectory(Path.Combine(Source, "empty"));
        repo = new(new AppPaths(root));
        repo.UpdateConfigAsync(new(Source, Path.Combine(root, "backups"), 30), default).GetAwaiter().GetResult();
        vss = new(root);
        compressor = new(new SevenZipCompressor(new AppPaths(AppContext.BaseDirectory)));
        service = new(repo, vss, new(), new AppPaths(root), compressor);
    }

    [Fact]
    public async Task FourArchivesRetainThreeAndReleaseOnlyTemporaryShadows()
    {
        vss.Shadows.Add(new("foreign", "foreign-device", "foreign-volume"));
        for (var i = 0; i < 4; i++) await service.RunOnceAsync(default);
        var runs = await repo.ListRunsAsync(default);
        Assert.Equal(3, runs.Count(x => x.Status == "Succeeded"));
        Assert.Equal("Deleted", runs.Last().Status);
        Assert.Single(vss.Shadows, x => x.Id == "foreign");
        Assert.Equal(4, vss.Deleted.Count);
        Assert.All(runs, x => Assert.Equal("Zip", x.Kind));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(root, "backups"), "*.zip").Length);
        Assert.All(runs.Where(x => x.Status == "Succeeded"), x => Assert.True(x.ArchiveBytes > 0));
    }

    [Fact]
    public async Task RestoresOldBytesAndEmptyDirectoriesAfterShadowIsGone()
    {
        File.WriteAllText(Path.Combine(Source, "unicode-\u4e2d\u6587.txt"), "unicode data");
        await service.RunOnceAsync(default);
        var run = (await repo.ListRunsAsync(default))[0];
        Assert.Equal("Succeeded", run.Status);
        File.WriteAllText(Path.Combine(Source, "live.txt"), "after");
        vss.FailList = true;
        await service.ReconcileAsync(default, true);
        var target = Path.Combine(root, "restored");
        var result = await service.RestoreAsync(run.Id, target, "", default);
        Assert.True(result.Success, result.Message);
        Assert.Equal("before", File.ReadAllText(Path.Combine(target, "live.txt")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.True(File.Exists(Path.Combine(target, "unicode-\u4e2d\u6587.txt")));
        Assert.False((await service.RestoreAsync(run.Id, target, "", default)).Success);
    }

    [Fact]
    public async Task FullRestoreReplacesSourceAndKeepsPreviousDirectory()
    {
        await service.RunOnceAsync(default);
        var run = (await repo.ListRunsAsync(default))[0];
        File.WriteAllText(Path.Combine(Source, "live.txt"), "after");
        var result = await service.ReplaceSourceAsync(run.Id, default);
        Assert.True(result.Success, result.Message);
        Assert.Equal("before", File.ReadAllText(Path.Combine(Source, "live.txt")));
        var old = Assert.Single(Directory.GetDirectories(root, "source.before-full-restore-*"));
        Assert.Equal("after", File.ReadAllText(Path.Combine(old, "live.txt")));
    }

    [Fact]
    public async Task SelectiveRestoreAndTraversalValidation()
    {
        await service.RunOnceAsync(default);
        var run = (await repo.ListRunsAsync(default))[0];
        Assert.Contains((await repo.ListFilesAsync(run.Id, "", default))!, x => x.Name == "live.txt");
        Assert.True((await service.RestoreAsync(run.Id, Path.Combine(root, "single"), "live.txt", default)).Success);
        Assert.False((await service.RestoreAsync(run.Id, Path.Combine(root, "bad"), "..", default)).Success);
        Assert.False(Directory.Exists(Path.Combine(root, "bad")));
    }

    [Fact]
    public async Task VerificationFailurePreservesPreviousArchiveAndCleansPartial()
    {
        await service.RunOnceAsync(default);
        var old = (await repo.ListRunsAsync(default))[0];
        compressor.FailVerify = true;
        await service.RunOnceAsync(default);
        Assert.Equal("Failed", (await repo.ListRunsAsync(default))[0].Status);
        Assert.True(File.Exists(old.SnapshotPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "backups"), "*.partial"));
        Assert.Empty(vss.Shadows);
    }

    [Fact]
    public async Task VssFailureDoesNotDeleteAnyCompletedBackup()
    {
        for (var i = 0; i < 3; i++) await service.RunOnceAsync(default);
        vss.FailCreate = true;
        await service.RunOnceAsync(default);
        Assert.Equal("Failed", (await repo.ListRunsAsync(default))[0].Status);
        Assert.Equal(3, (await repo.ListRunsAsync(default)).Count(x => x.Status == "Succeeded"));
    }

    [Fact]
    public async Task CrashDumpsAreIncludedByDefaultAndExcludedOnlyBySetting()
    {
        File.WriteAllText(Path.Combine(Source, "crash.MDMP"), "diagnostic dump");
        await service.RunOnceAsync(default);
        var first = (await repo.ListRunsAsync(default))[0];
        Assert.Equal("Succeeded", first.Status);
        using (var zip = ZipFile.OpenRead(first.SnapshotPath)) Assert.NotNull(zip.GetEntry("crash.MDMP"));
        await repo.UpdateConfigAsync(new(Source, Path.Combine(root, "backups"), 30, IncludeCrashDumps: false), default);
        await service.RunOnceAsync(default);
        var second = (await repo.ListRunsAsync(default))[0];
        Assert.Equal("Succeeded", second.Status);
        using (var zip = ZipFile.OpenRead(second.SnapshotPath)) Assert.Null(zip.GetEntry("crash.MDMP"));
        Assert.True(File.Exists(Path.Combine(Source, "crash.MDMP")));
    }

    [Fact]
    public async Task RestartDiscardsInterruptedArchiveAndReleasesOwnedShadow()
    {
        await service.RunOnceAsync(default);
        var id = await repo.StartRunAsync(DateTimeOffset.UtcNow, "", default);
        var shadow = await vss.CreateAsync(Source, default);
        await repo.AttachShadowAsync(id, Source, shadow, default);
        var archive = Path.Combine(root, "backups", "snapshot-interrupted.zip");
        await repo.SetArchiveAsync(id, archive, null, default);
        File.WriteAllText(archive + ".partial", "incomplete");
        await service.ReconcileAsync(default, true);
        Assert.Equal("Failed", (await repo.GetRunAsync(id, default))!.Status);
        Assert.False(File.Exists(archive + ".partial"));
        Assert.Contains(shadow.ShadowId, vss.Deleted);
        Assert.Single((await repo.ListRunsAsync(default)), x => x.Status == "Succeeded");
    }

    [Fact]
    public async Task RestartRecoversPublishedArchiveWhenDatabaseCommitWasInterrupted()
    {
        await service.RunOnceAsync(default);
        var previous = (await repo.ListRunsAsync(default))[0];
        var id = await repo.StartRunAsync(DateTimeOffset.UtcNow, "", default);
        var archive = Path.Combine(root, "backups", "snapshot-published.zip");
        File.Copy(previous.SnapshotPath, archive);
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(File.ReadAllText(previous.SnapshotPath + ".json"))! with { RunId = id };
        File.WriteAllText(archive + ".json", JsonSerializer.Serialize(manifest));
        await repo.SetArchiveAsync(id, archive, null, default);
        await service.ReconcileAsync(default, true);
        Assert.Equal("Succeeded", (await repo.GetRunAsync(id, default))!.Status);
    }

    [Fact]
    public async Task OldVssDeviceRenumberingRevivesUnavailableRecordById()
    {
        var id = await repo.StartRunAsync(DateTimeOffset.UtcNow, "", default);
        var shadow = await vss.CreateAsync(Source, default);
        await repo.AttachShadowAsync(id, Source, shadow, default);
        await repo.CompleteRunAsync(id, DateTimeOffset.UtcNow, "Unavailable", shadow.RootPath, 0, 0, 0, null, default);
        var changed = shadow.RootPath + "-renumbered";
        Directory.Move(shadow.RootPath, changed);
        vss.Shadows[0] = new(shadow.ShadowId, changed, shadow.Volume);
        await service.ReconcileAsync(default, true);
        var run = (await repo.GetRunAsync(id, default))!;
        Assert.Equal("Succeeded", run.Status);
        Assert.True(Directory.Exists(run.SnapshotPath));
        Assert.Empty(vss.Deleted);
    }

    [Fact]
    public async Task DamagedZipIsRejectedByNativeCrcVerification()
    {
        var archive = Path.Combine(root, "bad.zip");
        File.WriteAllText(archive, "not a zip");
        await Assert.ThrowsAsync<IOException>(() => compressor.Inner.VerifyAsync(archive, 1, _ => { }, default));
    }

    [Fact]
    public async Task CancellationStopsNativeCompression()
    {
        using (var large = File.Create(Path.Combine(Source, "large.bin"))) large.SetLength(1024L * 1024 * 1024);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compressor.Inner.CreateAsync(Source, Path.Combine(root, "cancel.zip"), 1, 1, _ => { }, cancel.Token));
        // The awaited call must release all file handles before cleanup/restart can happen.
        if (File.Exists(Path.Combine(root, "cancel.zip"))) File.Delete(Path.Combine(root, "cancel.zip"));
    }

    [Fact]
    public async Task LegacyZipStillRestoresAndDeletes()
    {
        var zip = Path.Combine(root, "backups", "snapshot-legacy.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("hello.txt").Open())) writer.Write("legacy");
        var id = await repo.StartRunAsync(DateTimeOffset.UtcNow, zip, default);
        await repo.CompleteRunAsync(id, DateTimeOffset.UtcNow, "Succeeded", zip, 1, 6, 1, null, default);
        Assert.Single((await repo.ListFilesAsync(id, "", default))!);
        Assert.True((await service.RestoreAsync(id, Path.Combine(root, "legacy"), "", default)).Success);
        await service.DeleteAsync(id, default);
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void CompressionSettingsValidateAndIntervalChangeDoesNotQueue()
    {
        Assert.NotNull(new BackupConfigUpdate(Source, root, 30, 0, 1).Validate());
        Assert.NotNull(new BackupConfigUpdate(Source, root, 30, 4, 9).Validate());
        service.SignalConfigurationChanged(7);
        Assert.InRange((service.GetStatus().NextRunAt!.Value - DateTimeOffset.UtcNow).TotalSeconds, 419, 421);
        Assert.False(service.GetStatus().IsQueued);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Path.GetFileName(root).StartsWith("autopackup-test-")) Directory.Delete(root, true);
    }

    private sealed class TestCompressor(SevenZipCompressor inner) : IArchiveCompressor
    {
        public SevenZipCompressor Inner => inner;
        public bool FailVerify;
        public Task<ArchiveStats> CreateAsync(string source, string archive, int threads, int level, Action<int> progress, CancellationToken ct, bool includeCrashDumps = true) => inner.CreateAsync(source, archive, threads, level, progress, ct, includeCrashDumps);
        public Task VerifyAsync(string archive, int threads, Action<int> progress, CancellationToken ct) => FailVerify ? throw new IOException("Simulated CRC failure") : inner.VerifyAsync(archive, threads, progress, ct);
    }

    private sealed class FakeVss(string root) : IVolumeSnapshotProvider
    {
        public List<ShadowInfo> Shadows { get; } = new();
        public List<string> Deleted { get; } = new();
        public bool FailCreate, FailList;
        public int Creations;
        public Task<SnapshotHandle> CreateAsync(string source, CancellationToken ct)
        {
            Creations++; ct.ThrowIfCancellationRequested();
            if (FailCreate) throw new VssException(6);
            var id = Guid.NewGuid().ToString("B");
            var path = Path.Combine(root, "shadow-" + Creations); Directory.CreateDirectory(path);
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(path, Path.GetRelativePath(source, dir)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(path, Path.GetRelativePath(source, file)));
            Shadows.Add(new(id, path, "test-volume"));
            return Task.FromResult(new SnapshotHandle(path, id, path, "test-volume"));
        }
        public Task DeleteAsync(string id, CancellationToken ct)
        {
            Deleted.Add(id);
            var shadow = Shadows.FirstOrDefault(x => x.Id == id);
            if (shadow != null) Directory.Delete(shadow.DevicePath, true);
            Shadows.RemoveAll(x => x.Id == id); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ShadowInfo>> ListAsync(CancellationToken ct) => FailList ? throw new IOException("VSS offline") : Task.FromResult<IReadOnlyList<ShadowInfo>>(Shadows.ToArray());
        public Task<IReadOnlyList<ShadowStorage>> StorageAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ShadowStorage>>(Array.Empty<ShadowStorage>());
    }
}
