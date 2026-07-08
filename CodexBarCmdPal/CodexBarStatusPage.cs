using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Globalization;

namespace CodexBarCmdPal;

internal sealed partial class CodexBarStatusPage : ListPage
{
    private static readonly IconInfo StatusIcon = new("\uE9D9");
    private readonly CodexBarStatusClient _client;
    private CodexBarStatusSnapshot? _snapshot;

    public CodexBarStatusPage(
        CodexBarStatusClient client,
        CodexBarStatusSnapshot? snapshot)
    {
        _client = client;
        _snapshot = snapshot;
        Icon = StatusIcon;
        Title = "CodexBar";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        _snapshot = _client.ReadSnapshotAsync().GetAwaiter().GetResult() ?? _snapshot;

        if (_snapshot?.Providers.Count > 0 != true)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "CodexBar offline",
                    Subtitle = "Start CodexBar Desktop to show live usage",
                    Icon = StatusIcon,
                },
            ];
        }

        return _snapshot.Providers
            .Select(provider => new ListItem(new NoOpCommand())
            {
                Title = $"{provider.Name} {provider.StatusText}",
                Subtitle = ProviderSubtitle(provider),
                Icon = StatusIcon,
            })
            .ToArray();
    }

    private static string ProviderSubtitle(CodexBarProviderSnapshot provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.Error))
        {
            return provider.Error;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(provider.Subtitle))
        {
            parts.Add(provider.Subtitle);
        }

        if (provider.ThirtyDayCost is double thirtyDayCost)
        {
            parts.Add($"30d ${thirtyDayCost:0.00}");
        }

        if (provider.ThirtyDayTokens is ulong thirtyDayTokens)
        {
            parts.Add($"{FormatCount(thirtyDayTokens)} tokens");
        }

        if (!string.IsNullOrWhiteSpace(provider.TopModel))
        {
            parts.Add(provider.TopModel);
        }

        return parts.Count == 0 ? "No local usage yet" : string.Join(" · ", parts);
    }

    private static string FormatCount(ulong value)
    {
        if (value >= 1_000_000_000)
        {
            return $"{value / 1_000_000_000d:0.#}B";
        }

        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
