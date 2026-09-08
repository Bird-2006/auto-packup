using AutoPackup;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using Xunit;

public sealed class SnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "autopackup-test-" + Guid.NewGuid().ToString("N"));
    private readonly BackupRepository repo;
    private readonly FakeVss vss;
    private readonly BackupService service;
    private string Source => Path.Combine(root, "source");

    public SnapshotTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "live.txt"), "before");
        repo = new(new AppPaths(root));
        repo.UpdateConfigAsync(new(Source, Path.Combine(root, "backups"), 30), default).GetAwaiter().GetResult();
        vss = new(root);
        service = new(repo, vss, new(), new AppPaths(root));
    }

    [Fact]
    public async Task FourRunsRetainThreeAndNeverDeleteForeignShadow()
    {
        vss.Shadows.Add(new("foreign", "foreign-device", "foreign-volume"));
        for (var i = 0; i < 4; i++) await service.RunOnceAsync(default);
        var runs = await repo.ListRunsAsync(default);
        Assert.Equal(3, runs.Count(x => x.Status == "Succeeded"));
        Assert.Equal("Deleted", runs.Last().Status);
        Assert.Contains(vss.Shadows, x => x.Id == "foreign");
        Assert.Single(vss.Deleted);
        Assert.All(runs, x => Assert.Equal("Vss", x.Kind));
    }

    [Fact]
    public async Task SnapshotRestoresOldBytesAndEmptyDirectoriesWithoutOverwriting()
    {
        await service.RunOnceAsync(default);
        var run = (await repo.ListRunsAsync(default))[0];
        Directory.CreateDirectory(Path.Combine(run.SnapshotPath, "empty"));
        File.WriteAllText(Path.Combine(Source, "live.txt"), "after");
        var destination = Path.Combine(root, "restored");
        var result = await service.RestoreAsync(run.Id, destination, "", default);
        Assert.True(result.Success, result.Message);
        Assert.Equal("before", File.ReadAllText(Path.Combine(destination, "live.txt")));
        Assert.True(Directory.Exists(Path.Combine(destination, "empty")));
        Assert.False((await service.RestoreAsync(run.Id, Source, "", default)).Success);
        Assert.False((await service.RestoreAsync(run.Id, destination, "", default)).Success);
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
        Assert.Single(Directory.GetDirectories(root, "source.before-full-restore-*"));
    }

    [Fact]
    public async Task BrowsingAndRestoreRejectTraversal()
    {
        await service.RunOnceAsync(default);
        var run = (await repo.ListRunsAsync(default))[0];
        Assert.Contains((await repo.ListFilesAsync(run.Id, "", default))!, x => x.Name == "live.txt");
        await Assert.ThrowsAsync<ArgumentException>(() => repo.ListFilesAsync(run.Id, "..", default));
        Assert.False((await service.RestoreAsync(run.Id, Path.Combine(root, "bad"), "..", default)).Success);
        Assert.False(Directory.Exists(Path.Combine(root, "bad")));
    }

    [Fact]
    public async Task MissingShadowAndReusedDevicePathAreUnavailable()
    {
        await service.RunOnceAsync(default);
        var run = (await repo.ListRunsAsync(default))[0];
        vss.Shadows.Clear();
        vss.Shadows.Add(new("another-id", run.DevicePath!, run.SourceVolume!));
        await service.ReconcileAsync(default, true);
        Assert.Equal("Unavailable", (await repo.GetRunAsync(run.Id, default))!.Status);
        Assert.False((await service.RestoreAsync(run.Id, Path.Combine(root, "bad"), "", default)).Success);
    }

    [Fact]
    public async Task RestartKeepsValidShadowAndRecoversInterruptedRun()
    {
        await service.RunOnceAsync(default);
        var complete = (await repo.ListRunsAsync(default))[0];
        var interrupted = await repo.StartRunAsync(DateTimeOffset.UtcNow, "", default);
        var handle = await vss.CreateAsync(Source, default);
        await repo.AttachShadowAsync(interrupted, Source, handle, default);
        await service.ReconcileAsync(default, true);
        Assert.Equal("Succeeded", (await repo.GetRunAsync(complete.Id, default))!.Status);
        Assert.Equal("Failed", (await repo.GetRunAsync(interrupted, default))!.Status);
        Assert.Contains(handle.ShadowId, vss.Deleted);
        Assert.DoesNotContain(complete.ShadowId!, vss.Deleted);
    }

    [Fact]
    public async Task FailedDeletionDoesNotEraseMetadataOrSuccess()
    {
        await service.RunOnceAsync(default);
        var run = (await repo.ListRunsAsync(default))[0];
        vss.FailDelete = true;
        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(run.Id, default));
        Assert.Equal("Succeeded", (await repo.GetRunAsync(run.Id, default))!.Status);
    }

    [Fact]
    public async Task LowSpaceRetriesOnceAndPreservesAtLeastOneExistingSnapshot()
    {
        await service.RunOnceAsync(default); await service.RunOnceAsync(default);
        vss.FailCreate = true;
        var before = vss.Creations;
        await service.RunOnceAsync(default);
        Assert.Equal(2, vss.Creations - before);
        Assert.Contains(await repo.ListRunsAsync(default), x => x.Status == "Succeeded");
        before = vss.Creations;
        await service.RunOnceAsync(default);
        Assert.Equal(1, vss.Creations - before);
        Assert.Contains(await repo.ListRunsAsync(default), x => x.Status == "Succeeded");
    }

    [Fact]
    public async Task LegacyZipRemainsBrowsableRestorableAndExplicitlyDeletable()
    {
        var zip = Path.Combine(root, "backups", "snapshot-legacy.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("hello.txt").Open())) writer.Write("legacy");
        var id = await repo.StartRunAsync(DateTimeOffset.UtcNow, zip, default);
        await repo.CompleteRunAsync(id, DateTimeOffset.UtcNow, "Succeeded", zip, 1, 6, 1, null, default);
        Assert.Single((await repo.ListFilesAsync(id, "", default))!);
        var result = await service.RestoreAsync(id, Path.Combine(root, "legacy-restore"), "", default);
        Assert.True(result.Success, result.Message);
        for (var i = 0; i < 4; i++) await service.RunOnceAsync(default);
        Assert.True(File.Exists(zip));
        await service.DeleteAsync(id, default);
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void IntervalChangeReschedulesWithoutImmediateSnapshot()
    {
        service.SignalConfigurationChanged(7);
        Assert.InRange((service.GetStatus().NextRunAt!.Value - DateTimeOffset.UtcNow).TotalSeconds, 419, 421);
        Assert.False(service.GetStatus().IsQueued);
        Assert.Equal(0, vss.Creations);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Path.GetFileName(root).StartsWith("autopackup-test-")) Directory.Delete(root, true);
    }

    private sealed class FakeVss(string root) : IVolumeSnapshotProvider
    {
        public List<ShadowInfo> Shadows { get; } = new();
        public List<string> Deleted { get; } = new();
        public bool FailDelete, FailCreate;
        public int Creations;
        public Task<SnapshotHandle> CreateAsync(string source, CancellationToken ct)
        {
            Creations++; ct.ThrowIfCancellationRequested();
            if (FailCreate) { FailCreate = false; throw new VssException(6); }
            var id = Guid.NewGuid().ToString("B");
            var path = Path.Combine(root, "shadow-" + Creations); Directory.CreateDirectory(path);
            File.Copy(Path.Combine(source, "live.txt"), Path.Combine(path, "live.txt"));
            Shadows.Add(new(id, path, "test-volume"));
            return Task.FromResult(new SnapshotHandle(path, id, path, "test-volume"));
        }
        public Task DeleteAsync(string id, CancellationToken ct)
        {
            if (FailDelete) throw new IOException("Simulated deletion failure");
            Deleted.Add(id); Shadows.RemoveAll(x => x.Id == id); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ShadowInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ShadowInfo>>(Shadows.ToArray());
        public Task<IReadOnlyList<ShadowStorage>> StorageAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ShadowStorage>>(Array.Empty<ShadowStorage>());
    }
}
