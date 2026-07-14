using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace CodexToys;

internal sealed class CodexToysSettings : JsonSettingsManager
{
    private const string RefreshIntervalId = "refreshIntervalSeconds";
    private const string ScanDaysId = "scanDays";
    private const string CustomSessionDirsId = "customSessionDirs";
    private readonly object _settingsIo = new();
    private int _loadStarted;

    public CodexToysSettings()
    {
        FilePath = SettingsFilePath();

        Settings.Add(new TextSetting(
            RefreshIntervalId,
            "Refresh interval",
            "Seconds between local log scans",
            "60"));
        Settings.Add(new TextSetting(
            ScanDaysId,
            "Scan days",
            "Number of recent days to scan",
            "30"));
        Settings.Add(new TextSetting(
            CustomSessionDirsId,
            "Codex session dirs",
            "Extra session directories separated by new lines or semicolons",
            ""));

        Settings.SettingsChanged += (_, _) => QueueSaveSettings();
        Settings.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler? SettingsChanged;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(ReadInt(RefreshIntervalId, 60, 5, 600));

    public int ScanDays => ReadInt(ScanDaysId, 30, 1, 365);

    public IReadOnlyList<string> CustomSessionDirs
    {
        get
        {
            try
            {
                return (Settings.GetSetting<string>(CustomSessionDirsId) ?? "")
                    .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                ExtensionLog.Write($"Failed to read custom session dirs setting: {ex.GetType().Name}: {ex.Message}");
                return [];
            }
        }
    }

    private int ReadInt(string id, int fallback, int min, int max)
    {
        string? raw;
        try
        {
            raw = Settings.GetSetting<string>(id);
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Failed to read setting {id}: {ex.GetType().Name}: {ex.Message}");
            return fallback;
        }
        if (!int.TryParse(raw, out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private void OnSettingsChanged(object sender, Settings args)
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StartLoading()
    {
        if (Interlocked.Exchange(ref _loadStarted, 1) == 0)
        {
            _ = Task.Run(LoadSettingsInBackground);
        }
    }

    private void QueueSaveSettings()
    {
        _ = Task.Run(() =>
        {
            lock (_settingsIo)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    SaveSettings();
                }
                catch (Exception ex)
                {
                    ExtensionLog.Write($"Failed to save settings: {ex.GetType().Name}: {ex.Message}");
                }
            }
        });
    }

    private void LoadSettingsInBackground()
    {
        lock (_settingsIo)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                LoadSettings();
            }
            catch (Exception ex)
            {
                ExtensionLog.Write($"Failed to load settings: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string SettingsFilePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexToys");
        return Path.Combine(directory, "settings.json");
    }
}
