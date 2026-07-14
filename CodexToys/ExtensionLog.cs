using System.Threading.Channels;

namespace CodexToys;

internal static class ExtensionLog
{
    private const long MaxLogFileBytes = 1024 * 1024;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexToys",
        "extension.log");
    private static readonly string BackupLogPath = $"{LogPath}.1";
    private static readonly Channel<string> Messages = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private static readonly Task Writer = Task.Run(WriteLoopAsync);

    public static void Write(string message)
    {
        Messages.Writer.TryWrite($"{DateTimeOffset.Now:O} {message}");
    }

    private static async Task WriteLoopAsync()
    {
        var batch = new List<string>(32);
        while (await Messages.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 32 && Messages.Reader.TryRead(out var message))
            {
                batch.Add(message);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                RotateIfNeeded();
                File.AppendAllLines(LogPath, batch);
            }
            catch
            {
            }
        }
    }

    private static void RotateIfNeeded()
    {
        var log = new FileInfo(LogPath);
        if (log.Exists && log.Length >= MaxLogFileBytes)
        {
            File.Move(LogPath, BackupLogPath, true);
        }
    }
}
