namespace AutoPackup;

public static class PathRules
{
    public static string LocalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\"))
            throw new ArgumentException("An absolute local drive path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectLinks(full);
        return full;
    }

    public static bool Contains(string root, string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new ArgumentException("A relative path is required.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Contains(root, full)) throw new ArgumentException("Path is outside the snapshot.");
        RejectLinks(full);
        return full;
    }

    public static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Junctions and symbolic links cannot be followed: " + current);
    }
}
