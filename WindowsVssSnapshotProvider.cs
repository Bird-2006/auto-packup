using System.Management;

namespace AutoPackup;

public sealed record SnapshotHandle(string RootPath, string ShadowId, string DevicePath, string Volume);

public interface IVolumeSnapshotProvider
{
    Task<SnapshotHandle> CreateAsync(string sourceDirectory, CancellationToken ct);
    Task DeleteAsync(string shadowId, CancellationToken ct);
    Task<IReadOnlyList<ShadowInfo>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<ShadowStorage>> StorageAsync(CancellationToken ct);
}

public sealed class VssException(uint code) : IOException($"VSS creation failed (Win32_ShadowCopy code {code}).")
{
    public uint Code { get; } = code;
}

public sealed class WindowsVssSnapshotProvider : IVolumeSnapshotProvider
{
    // ClientAccessible produces persistent, crash-consistent snapshots without writer coordination.
    public Task<SnapshotHandle> CreateAsync(string sourceDirectory, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var source = PathRules.LocalDirectory(sourceDirectory);
        var root = Path.GetPathRoot(source)!;
        using var type = new ManagementClass("Win32_ShadowCopy");
        using var input = type.GetMethodParameters("Create");
        input["Volume"] = root;
        input["Context"] = "ClientAccessible";
        // Do not abandon an in-flight WMI creation: its returned ID must be recorded or cleaned up.
        using var result = type.InvokeMethod("Create", input, null);
        var code = Convert.ToUInt32(result["ReturnValue"]);
        if (code != 0) throw new VssException(code);
        var id = (string)result["ShadowID"];
        try
        {
            using var shadow = Open(id);
            shadow.Get();
            var device = (string)shadow["DeviceObject"];
            return new SnapshotHandle(Path.Combine(device + "\\", Path.GetRelativePath(root, source)), id, device, (string)shadow["VolumeName"]);
        }
        catch
        {
            using var shadow = Open(id);
            shadow.Delete();
            throw;
        }
    });

    public Task DeleteAsync(string shadowId, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var shadow = Open(shadowId);
        try { shadow.Delete(); }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound) { }
    }, ct);

    public Task<IReadOnlyList<ShadowInfo>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<ShadowInfo>>(() =>
    {
        using var query = new ManagementObjectSearcher("SELECT ID, DeviceObject, VolumeName FROM Win32_ShadowCopy");
        using var rows = query.Get();
        var list = new List<ShadowInfo>();
        foreach (ManagementObject row in rows)
        {
            using (row) { ct.ThrowIfCancellationRequested(); list.Add(new((string)row["ID"], (string)row["DeviceObject"], (string)row["VolumeName"])); }
        }
        return list;
    }, ct);

    public Task<IReadOnlyList<ShadowStorage>> StorageAsync(CancellationToken ct) => Task.Run<IReadOnlyList<ShadowStorage>>(() =>
    {
        using var query = new ManagementObjectSearcher("SELECT * FROM Win32_ShadowStorage");
        using var rows = query.Get();
        var list = new List<ShadowStorage>();
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                ct.ThrowIfCancellationRequested();
                using var volume = new ManagementObject((string)row["Volume"]);
                using var storage = new ManagementObject((string)row["DiffVolume"]);
                var maximum = Convert.ToUInt64(row["MaxSpace"]);
                list.Add(new((string)volume["Name"], (string)storage["Name"], Convert.ToUInt64(row["UsedSpace"]), Convert.ToUInt64(row["AllocatedSpace"]), maximum == ulong.MaxValue ? null : maximum, Convert.ToUInt64(storage["FreeSpace"])));
            }
        }
        return list;
    }, ct);

    private static ManagementObject Open(string id) => new($"Win32_ShadowCopy.ID='{Guid.Parse(id):B}'");
}
