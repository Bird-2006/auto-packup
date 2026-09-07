namespace AutoPackup;

public sealed class AppPaths
{
    public string DataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "data");
    public string DatabasePath => Path.Combine(DataDirectory, "autopackup.db");
    public string BackupDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "backups");
}
