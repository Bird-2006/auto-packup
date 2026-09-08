namespace AutoPackup;

public sealed class AppPaths
{
    public AppPaths() : this(AppContext.BaseDirectory) { }
    public AppPaths(string root) { ApplicationDirectory = root; DataDirectory = Path.Combine(root, "data"); BackupDirectory = Path.Combine(root, "backups"); }
    public string ApplicationDirectory { get; }
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "autopackup.db");
    public string RuntimeMarkerPath => Path.Combine(DataDirectory, "runtime-marker.json");
    public string BackupDirectory { get; }
}
