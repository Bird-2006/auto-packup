using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoPackup;

public sealed record ArchiveStats(long Files, long SourceBytes, long ArchiveBytes);
public sealed record ArchiveManifest(int Version, long RunId, string SourceDirectory, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, long DurationMs, ArchiveStats Stats, bool IncludeCrashDumps = true);

public interface IArchiveCompressor
{
    Task<ArchiveStats> CreateAsync(string source, string destination, int threads, int level, Action<int> progress, CancellationToken ct, bool includeCrashDumps = true);
    Task VerifyAsync(string archive, int threads, Action<int> progress, CancellationToken ct);
}

public sealed class SevenZipCompressor(AppPaths paths) : IArchiveCompressor
{
    public async Task<ArchiveStats> CreateAsync(string source, string destination, int threads, int level, Action<int> progress, CancellationToken ct, bool includeCrashDumps = true)
    {
        if (File.Exists(destination)) throw new IOException("Archive destination already exists.");
        // Fail explicitly on links instead of following a mount/junction outside the frozen source volume.
        var pending = new Stack<string>(); pending.Push(source);
        long files = 0, bytes = 0, directories = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (!includeCrashDumps && IsCrashDump(entry.Name)) continue;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Snapshot contains an unsupported reparse point: " + entry.FullName);
                if (entry is DirectoryInfo) { directories++; pending.Push(entry.FullName); }
                else if (includeCrashDumps || !IsCrashDump(entry.Name)) { files++; bytes = checked(bytes + ((FileInfo)entry).Length); }
            }
        }
        var free = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
        // Do not assume a compression ratio or delete successful backups before the new one is valid.
        var required = checked(bytes + bytes / 100 + 1024L * 1024 * 1024);
        if (free < required) throw new IOException($"Backup needs up to {required / 1073741824.0:F1} GiB working space; available {free / 1073741824.0:F1} GiB. Existing backups were preserved.");

        var link = destination + ".source";
        try
        {
            // Native archivers need a regular Win32 working directory for a GLOBALROOT shadow device.
            var working = source;
            if (source.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateSymbolicLink(link, source);
                working = link;
            }
            var arguments = new List<string> { "a", "-tzip", "-mm=Deflate", "-mcu=on", $"-mx={level}", $"-mmt={threads}", "-sse", "-r", "-y", destination, "*" };
            if (!includeCrashDumps) arguments.AddRange(["-xr!*.mdmp", "-xr!*.hprof"]);
            await RunAsync(arguments.ToArray(), working, progress, ct);
            using var zip = ZipFile.OpenRead(destination);
            if (zip.Entries.LongCount(x => x.Name.Length != 0) != files || zip.Entries.Sum(x => x.Length) != bytes || zip.Entries.LongCount(x => x.Name.Length == 0) < directories)
                throw new InvalidDataException("Archive file/directory inventory does not match the snapshot.");
            return new(files, bytes, new FileInfo(destination).Length);
        }
        finally { if (Directory.Exists(link) || new DirectoryInfo(link).LinkTarget != null) Directory.Delete(link); }
    }

    public Task VerifyAsync(string archive, int threads, Action<int> progress, CancellationToken ct) =>
        RunAsync(["t", $"-mmt={threads}", archive], Path.GetDirectoryName(archive)!, progress, ct);

    private static bool IsCrashDump(string path) => Path.GetExtension(path).Equals(".mdmp", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".hprof", StringComparison.OrdinalIgnoreCase);

    private async Task RunAsync(string[] args, string working, Action<int> progress, CancellationToken ct)
    {
        var executable = Path.Combine(paths.ApplicationDirectory, "tools", "7zip", "7za.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Run scripts/Install-CompressionTool.ps1, then publish again.", executable);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = working, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var arg in new[] { "-bsp1", "-bso1", "-bse2", "-sccUTF-8" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not launch 7-Zip.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
        var output = new StringBuilder();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var match = Regex.Match(line, @"(?<!\d)(\d{1,3})%");
                if (match.Success) progress(Math.Min(100, int.Parse(match.Groups[1].Value)));
                else lock (output) { if (output.Length < 8000) output.AppendLine(line); }
            }
        }
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally { await Task.WhenAll(stdout, stderr); }
        // Exit 1 is a warning (e.g. unreadable files), not a successful backup.
        if (process.ExitCode != 0) throw new IOException($"7-Zip failed ({process.ExitCode}): {output}");
        progress(100);
    }
}
