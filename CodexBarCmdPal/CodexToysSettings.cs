using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace CodexBarCmdPal;

internal sealed class CodexToysSettings
{
    private const string RefreshIntervalId = "refreshIntervalSeconds";
    private const string ScanDaysId = "scanDays";
    private const string CustomSessionDirsId = "customSessionDirs";
    private readonly Settings _settings = new();

    public CodexToysSettings()
    {
        _settings.Add(new TextSetting(
            RefreshIntervalId,
            "Refresh interval",
            "Seconds between local log scans",
            "15"));
        _settings.Add(new TextSetting(
            ScanDaysId,
            "Scan days",
            "Number of recent days to scan",
            "30"));
        _settings.Add(new TextSetting(
            CustomSessionDirsId,
            "Codex session dirs",
            "Extra session directories separated by new lines or semicolons",
            ""));
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler? SettingsChanged;

    public ICommandSettings Settings => _settings;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(ReadInt(RefreshIntervalId, 15, 5, 600));

    public int ScanDays => ReadInt(ScanDaysId, 30, 1, 365);

    public IReadOnlyList<string> CustomSessionDirs
    {
        get
        {
            try
            {
                return (_settings.GetSetting<string>(CustomSessionDirsId) ?? "")
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
            raw = _settings.GetSetting<string>(id);
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
}
