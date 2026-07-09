using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexToys;

internal sealed partial class CodexToysCommandsProvider : CommandProvider
{
    private static readonly IconInfo CodexIcon =
        IconHelpers.FromRelativePaths("Assets/codex-light.svg", "Assets/codex-dark.svg");
    private static readonly IconInfo TodayIcon =
        IconHelpers.FromRelativePaths("Assets/calendar-1-light.svg", "Assets/calendar-1-dark.svg");
    private static readonly IconInfo ThirtyDayIcon =
        IconHelpers.FromRelativePaths("Assets/calendar-days-light.svg", "Assets/calendar-days-dark.svg");
    private static readonly IconInfo TokensIcon =
        IconHelpers.FromRelativePaths("Assets/coins-light.svg", "Assets/coins-dark.svg");
    private readonly CodexToysSettings _settings = new();
    private readonly CodexToysStatusClient _client;
    private readonly CodexToysStatusPage _statusPage;
    private readonly ICommandItem[] _topLevelCommands;
    private readonly ICommandItem[] _dockBands;
    private readonly ListItem _usageItem;
    private readonly ListItem _todayItem;
    private readonly ListItem _thirtyDayItem;
    private readonly ListItem _tokensItem;
    private readonly Timer _refreshTimer;
    private CodexToysStatusSnapshot? _snapshot;

    public CodexToysCommandsProvider()
    {
        Id = "CodexToys";
        DisplayName = "CodexToys";
        Icon = CodexIcon;
        Settings = _settings.Settings;
        ExtensionLog.Write("CodexToysCommandsProvider constructed");
        _client = new CodexToysStatusClient(_settings);
        _settings.SettingsChanged += OnSettingsChanged;

        _statusPage = new CodexToysStatusPage(_client, _snapshot, CodexToysDetailMode.Overview, CodexIcon)
        {
            Id = "codextoys.page.status",
        };
        _topLevelCommands =
        [
            new CommandItem(_statusPage)
            {
                Title = "CodexToys",
                Subtitle = "Open usage details",
                Icon = CodexIcon,
            },
        ];

        _usageItem = DockItem("codextoys.dock.usage", "--", "Codex", CodexIcon, CodexToysDetailMode.Overview);
        _todayItem = DockItem("codextoys.dock.today", "--", "Today", TodayIcon, CodexToysDetailMode.TodayCost);
        _thirtyDayItem = DockItem("codextoys.dock.thirtyDay", "--", "30d", ThirtyDayIcon, CodexToysDetailMode.ThirtyDayCost);
        _tokensItem = DockItem("codextoys.dock.tokens", "--", "Tokens", TokensIcon, CodexToysDetailMode.Tokens);
        _dockBands =
        [
            new WrappedDockItem(
                [_usageItem, _todayItem, _thirtyDayItem, _tokensItem],
                "codextoys.dock.band",
                "CodexToys"),
        ];

        _refreshTimer = new Timer(
            _ => QueueRefresh(),
            null,
            TimeSpan.FromSeconds(1),
            _settings.RefreshInterval);
    }

    public override ICommandItem[] TopLevelCommands()
    {
        ExtensionLog.Write("TopLevelCommands requested");
        return _topLevelCommands;
    }

    public override ICommandItem[] GetDockBands()
    {
        ExtensionLog.Write("GetDockBands requested");
        return _dockBands;
    }

    public override ICommandItem? GetCommandItem(string id)
    {
        return id switch
        {
            "codextoys.dock.band" => _dockBands[0],
            "codextoys.dock.usage" => _usageItem,
            "codextoys.dock.today" => _todayItem,
            "codextoys.dock.thirtyDay" => _thirtyDayItem,
            "codextoys.dock.tokens" => _tokensItem,
            "codextoys.page.status" => _topLevelCommands[0],
            _ => null,
        };
    }

    public override void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        _refreshTimer.Dispose();
        base.Dispose();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _client.ClearCache();
        _refreshTimer.Change(TimeSpan.FromSeconds(1), _settings.RefreshInterval);
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ExtensionLog.Write($"Refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private async Task RefreshAsync()
    {
        var next = await _client.ReadSnapshotAsync().ConfigureAwait(false);
        if (next is not null)
        {
            _snapshot = next;
        }

        UpdateDockItems();
    }

    private static CodexToysProviderSnapshot? PreferredProvider(CodexToysStatusSnapshot? snapshot)
    {
        if (snapshot?.Providers.Count > 0 != true)
        {
            return null;
        }

        return snapshot.Providers.FirstOrDefault(provider => provider.Id == "codex")
            ?? snapshot.Providers.FirstOrDefault(provider => provider.Error is null)
            ?? snapshot.Providers[0];
    }

    private static string OfflineSubtitle(CodexToysStatusSnapshot? snapshot)
    {
        return snapshot is null
            ? "No local usage"
            : $"Updated {snapshot.UpdatedAt ?? "recently"}";
    }

    private ListItem DockItem(string id, string title, string subtitle, IconInfo icon, CodexToysDetailMode mode)
    {
        var command = new CodexToysStatusPage(_client, _snapshot, mode, icon)
        {
            Id = id,
            Name = title,
        };

        return new ListItem(command)
        {
            Title = title,
            Subtitle = subtitle,
            Icon = icon,
        };
    }

    private void UpdateDockItems()
    {
        var status = PreferredProvider(_snapshot);
        if (status is null)
        {
            SetItem(_usageItem, "--", OfflineSubtitle(_snapshot));
            SetItem(_todayItem, "--", "Today");
            SetItem(_thirtyDayItem, "--", "30d");
            SetItem(_tokensItem, "--", "Tokens");
            return;
        }

        SetItem(_usageItem, status.StatusText, SecondaryUsage(status));
        SetItem(_todayItem, FormatUsd(status.TodayCost), "Today");
        SetItem(_thirtyDayItem, FormatUsd(status.ThirtyDayCost), "30d");
        SetItem(_tokensItem, FormatCount(status.ThirtyDayTokens), "Tokens");
    }

    private static void SetItem(CommandItem item, string title, string subtitle)
    {
        if (item.Title != title)
        {
            item.Title = title;
        }

        if (item.Subtitle != subtitle)
        {
            item.Subtitle = subtitle;
        }
    }

    private static string SecondaryUsage(CodexToysProviderSnapshot provider)
    {
        if (provider.Secondary is not null && !string.IsNullOrWhiteSpace(provider.SecondaryLabel))
        {
            return $"{provider.SecondaryLabel} {provider.Secondary.UsedPercent:0}%";
        }

        return provider.Subtitle ?? provider.Primary?.ResetDescription ?? "";
    }

    private static string FormatUsd(double? value)
    {
        return value is double amount ? $"${amount:0.00}" : "--";
    }

    private static string FormatCount(ulong? value)
    {
        if (value is not ulong count)
        {
            return "--";
        }

        if (count >= 1_000_000_000)
        {
            return $"{count / 1_000_000_000d:0.#}B";
        }

        if (count >= 1_000_000)
        {
            return $"{count / 1_000_000d:0.#}M";
        }

        if (count >= 1_000)
        {
            return $"{count / 1_000d:0.#}K";
        }

        return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
