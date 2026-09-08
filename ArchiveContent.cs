using System.IO.Compression;

namespace AutoPackup;

public static class ArchiveContent
{
    public static async Task<long> ExtractAsync(string archivePath, string destination, string relative, Action onFile, CancellationToken ct)
    {
        var filter = relative.Replace('\\', '/').TrimEnd('/');
        PathRules.Resolve(destination, filter);
        using var archive = ZipFile.OpenRead(archivePath);
        long files = 0;
        var found = filter.Length == 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var selectedFile = name.Equals(filter, StringComparison.OrdinalIgnoreCase);
            if (filter.Length != 0 && !selectedFile && !name.StartsWith(filter + "/", StringComparison.OrdinalIgnoreCase)) continue;
            PathRules.Resolve(destination, name);
            found = true;
            var outputName = filter.Length == 0 ? name : selectedFile ? Path.GetFileName(filter) : name[(filter.Length + 1)..];
            if (outputName.Length == 0) continue;
            var target = PathRules.Resolve(destination, outputName);
            if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                await input.CopyToAsync(output, ct);
            File.SetLastWriteTimeUtc(target, entry.LastWriteTime.UtcDateTime);
            files++; onFile();
        }
        if (!found) throw new FileNotFoundException("Selected archive item was not found.", relative);
        return files;
    }
}
