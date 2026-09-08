using AutoPackup;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(options => options.ServiceName = "AutoPackup");
builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<LogBuffer>();
builder.Services.AddSingleton<BackupRepository>();
builder.Services.AddSingleton<IArchiveCompressor, SevenZipCompressor>();
builder.Services.AddSingleton<IVolumeSnapshotProvider, WindowsVssSnapshotProvider>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<BackupWorker>();

var app = builder.Build();
var paths = app.Services.GetRequiredService<AppPaths>();
Directory.CreateDirectory(paths.DataDirectory);
Directory.CreateDirectory(paths.BackupDirectory);
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (IOException ex) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
});

app.MapGet("/api/config", async (BackupRepository repo, CancellationToken ct) => Results.Ok(await repo.GetConfigAsync(ct)));
app.MapPut("/api/config", async (BackupConfigUpdate update, BackupRepository repo, BackupService service, CancellationToken ct) =>
{
    var error = update.Validate();
    if (error is not null) return Results.BadRequest(new { error });
    var config = await repo.UpdateConfigAsync(update, ct);
    service.SignalConfigurationChanged(config.IntervalMinutes);
    return Results.Ok(config);
});
app.MapGet("/api/status", (BackupService service) => Results.Ok(service.GetStatus()));
app.MapGet("/api/recovery", async (BackupService service, CancellationToken ct) => Results.Ok(await service.GetRecoveryInfoAsync(ct)));
app.MapGet("/api/storage", async (IVolumeSnapshotProvider provider, CancellationToken ct) => Results.Ok(await provider.StorageAsync(ct)));
app.MapGet("/api/archive-storage", async (BackupRepository repo, CancellationToken ct) =>
{
    var config = await repo.GetConfigAsync(ct);
    var drive = new DriveInfo(Path.GetPathRoot(config.BackupDirectory)!);
    return Results.Ok(new { directory = config.BackupDirectory, freeBytes = drive.AvailableFreeSpace, compressionThreads = config.CompressionThreads, compressionLevel = config.CompressionLevel });
});
app.MapPost("/api/backups/run", async (BackupService service, CancellationToken ct) =>
{
    var accepted = service.TryQueueManualRun();
    await Task.CompletedTask;
    return accepted ? Results.Accepted("/api/status") : Results.Conflict(new { error = "A backup is already running or queued." });
});
app.MapPost("/api/backups/cancel", (BackupService service) =>
{
    service.CancelCurrentRun();
    return Results.Accepted();
});
app.MapGet("/api/backups", async (BackupRepository repo, CancellationToken ct) => Results.Ok(await repo.ListRunsAsync(ct)));
app.MapDelete("/api/backups/{id:long}", async (long id, BackupService service, CancellationToken ct) =>
    await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/backups/{id:long}/files", async (long id, string? path, BackupRepository repo, CancellationToken ct) =>
{
    var result = await repo.ListFilesAsync(id, path ?? string.Empty, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/backups/{id:long}/restore", async (long id, RestoreRequest request, BackupService service, CancellationToken ct) =>
{
    if (!request.ReplaceOriginal && !request.ReplaceSource && string.IsNullOrWhiteSpace(request.Destination)) return Results.BadRequest(new { error = "Destination is required." });
    if (request.ReplaceOriginal)
    {
        if (!request.DatabaseStopped) return Results.BadRequest(new { error = "Confirm that the database service is stopped before replacing the original file." });
        var replaceResult = await service.ReplaceOriginalAsync(id, request.Path ?? string.Empty, ct);
        return replaceResult.Success ? Results.Ok(replaceResult) : Results.BadRequest(replaceResult);
    }
    if (request.ReplaceSource)
    {
        if (!request.DatabaseStopped) return Results.BadRequest(new { error = "Confirm that every service using the source directory is stopped before a full restore." });
        var sourceResult = await service.ReplaceSourceAsync(id, ct);
        return sourceResult.Success ? Results.Ok(sourceResult) : Results.BadRequest(sourceResult);
    }
    var result = await service.RestoreAsync(id, request.Destination, request.Path ?? string.Empty, ct);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapGet("/api/logs", (LogBuffer logs) => Results.Ok(logs.Snapshot()));

app.Services.GetRequiredService<LogBuffer>().Attach(app.Services.GetRequiredService<ILoggerFactory>());
app.Run();
