using AutoPackup;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "AutoPackup");
builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<LogBuffer>();
builder.Services.AddSingleton<BackupRepository>();
builder.Services.AddSingleton<IVolumeSnapshotProvider, WindowsVssSnapshotProvider>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<BackupWorker>();

var app = builder.Build();
var paths = app.Services.GetRequiredService<AppPaths>();
Directory.CreateDirectory(paths.DataDirectory);
Directory.CreateDirectory(paths.BackupDirectory);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", async (BackupRepository repo, CancellationToken ct) => Results.Ok(await repo.GetConfigAsync(ct)));
app.MapPut("/api/config", async (BackupConfigUpdate update, BackupRepository repo, BackupService service, CancellationToken ct) =>
{
    var error = update.Validate();
    if (error is not null) return Results.BadRequest(new { error });
    var config = await repo.UpdateConfigAsync(update, ct);
    service.SignalConfigurationChanged();
    return Results.Ok(config);
});
app.MapGet("/api/status", (BackupService service) => Results.Ok(service.GetStatus()));
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
app.MapDelete("/api/backups/{id:long}", async (long id, BackupRepository repo, CancellationToken ct) =>
    await repo.DeleteRunAsync(id, ct) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/backups/{id:long}/files", async (long id, string? path, BackupRepository repo, CancellationToken ct) =>
{
    var result = await repo.ListFilesAsync(id, path ?? string.Empty, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
app.MapPost("/api/backups/{id:long}/restore", async (long id, RestoreRequest request, BackupService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Destination)) return Results.BadRequest(new { error = "Destination is required." });
    var result = await service.RestoreAsync(id, request.Destination, ct);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});
app.MapGet("/api/logs", (LogBuffer logs) => Results.Ok(logs.Snapshot()));

app.Services.GetRequiredService<LogBuffer>().Attach(app.Services.GetRequiredService<ILoggerFactory>());
app.Run();
