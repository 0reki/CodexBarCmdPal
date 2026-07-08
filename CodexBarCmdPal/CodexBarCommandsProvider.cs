using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexBarCmdPal;

internal sealed partial class CodexBarCommandsProvider : CommandProvider
{
    private static readonly IconInfo CodexIcon =
        IconHelpers.FromRelativePaths("Assets/codex-light.svg", "Assets/codex-dark.svg");
    private static readonly IconInfo TodayIcon =
        IconHelpers.FromRelativePaths("Assets/calendar-1-light.svg", "Assets/calendar-1-dark.svg");
    private static readonly IconInfo ThirtyDayIcon =
        IconHelpers.FromRelativePaths("Assets/calendar-days-light.svg", "Assets/calendar-days-dark.svg");
    private static readonly IconInfo TokensIcon =
        IconHelpers.FromRelativePaths("Assets/coins-light.svg", "Assets/coins-dark.svg");
    private readonly CodexBarStatusClient _client = new();
    private readonly ICommandItem[] _topLevelCommands;
    private readonly ICommandItem[] _dockBands;
    private readonly ListItem _usageItem;
    private readonly ListItem _todayItem;
    private readonly ListItem _thirtyDayItem;
    private readonly ListItem _tokensItem;
    private readonly Timer _refreshTimer;
    private CodexBarStatusSnapshot? _snapshot;

    public CodexBarCommandsProvider()
    {
        Id = "CodexBar";
        DisplayName = "CodexBar";
        Icon = CodexIcon;
        ExtensionLog.Write("CodexBarCommandsProvider constructed");

        var statusPage = new CodexBarStatusPage(_client, _snapshot)
        {
            Id = "codexbar.page.status",
        };
        _topLevelCommands =
        [
            new CommandItem(statusPage)
            {
                Title = "CodexBar",
                Subtitle = "Open usage details",
                Icon = CodexIcon,
            },
        ];

        _usageItem = DockItem("codexbar.dock.usage", "--", "Codex", CodexIcon);
        _todayItem = DockItem("codexbar.dock.today", "--", "Today", TodayIcon);
        _thirtyDayItem = DockItem("codexbar.dock.thirtyDay", "--", "30d", ThirtyDayIcon);
        _tokensItem = DockItem("codexbar.dock.tokens", "--", "Tokens", TokensIcon);
        _dockBands =
        [
            new WrappedDockItem(
                [_usageItem, _todayItem, _thirtyDayItem, _tokensItem],
                "codexbar.dock.band",
                "CodexBar"),
        ];

        _refreshTimer = new Timer(
            _ => _ = RefreshAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5));
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
            "codexbar.dock.band" => _dockBands[0],
            "codexbar.dock.usage" => _usageItem,
            "codexbar.dock.today" => _todayItem,
            "codexbar.dock.thirtyDay" => _thirtyDayItem,
            "codexbar.dock.tokens" => _tokensItem,
            "codexbar.page.status" => _topLevelCommands[0],
            _ => null,
        };
    }

    public override void Dispose()
    {
        _refreshTimer.Dispose();
        base.Dispose();
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

    private static CodexBarProviderSnapshot? PreferredProvider(CodexBarStatusSnapshot? snapshot)
    {
        if (snapshot?.Providers.Count > 0 != true)
        {
            return null;
        }

        return snapshot.Providers.FirstOrDefault(provider => provider.Id == "codex")
            ?? snapshot.Providers.FirstOrDefault(provider => provider.Error is null)
            ?? snapshot.Providers[0];
    }

    private static string OfflineSubtitle(CodexBarStatusSnapshot? snapshot)
    {
        return snapshot is null
            ? "CodexBar offline"
            : $"Updated {snapshot.UpdatedAt ?? "recently"}";
    }

    private static ListItem DockItem(string id, string title, string subtitle, IconInfo icon)
    {
        return new ListItem(new NoOpCommand { Id = id, Name = title })
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

    private static string SecondaryUsage(CodexBarProviderSnapshot provider)
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
