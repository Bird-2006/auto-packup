namespace AutoPackup;

public sealed class AppPaths
{
    public AppPaths() : this(AppContext.BaseDirectory) { }
    public AppPaths(string root) { DataDirectory = Path.Combine(root, "data"); BackupDirectory = Path.Combine(root, "backups"); }
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "autopackup.db");
    public string BackupDirectory { get; }
}
