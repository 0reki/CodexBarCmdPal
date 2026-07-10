namespace CodexToys;

internal static class ExtensionLog
{
    private const long MaxLogFileBytes = 1024 * 1024;
    private static readonly object SyncRoot = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexToys",
        "extension.log");
    private static readonly string BackupLogPath = $"{LogPath}.1";

    public static void Write(string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                RotateIfNeeded();
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private static void RotateIfNeeded()
    {
        var log = new FileInfo(LogPath);
        if (!log.Exists || log.Length < MaxLogFileBytes)
        {
            return;
        }

        File.Move(LogPath, BackupLogPath, true);
    }
}
