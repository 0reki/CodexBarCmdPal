using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexToys;

internal sealed partial class CodexToysStatusPage : ContentPage
{
    private const string CodexSettingsUrl = "https://chatgpt.com/codex/cloud/settings/analytics";
    private static readonly IconInfo StatusIcon = new("\uE9D9");
    private readonly CodexToysStatusClient _client;
    private readonly CodexToysDetailMode _mode;
    private readonly FormContent _content = new()
    {
        TemplateJson = CardTemplate,
    };
    private CodexToysStatusSnapshot? _snapshot;

    public CodexToysStatusPage(
        CodexToysStatusClient client,
        CodexToysStatusSnapshot? snapshot,
        CodexToysDetailMode mode = CodexToysDetailMode.Overview,
        IconInfo? icon = null)
    {
        _client = client;
        _mode = mode;
        _snapshot = snapshot;
        Icon = icon ?? StatusIcon;
        Title = TitleForMode(mode);
        Name = "Open";
        Commands =
        [
            new CommandContextItem(new RefreshStatusCommand(this))
            {
                Title = "Refresh",
                Subtitle = "Reload local Codex usage",
            },
            new CommandContextItem(new OpenCodexSettingsCommand())
            {
                Title = "Open Codex Settings",
                Subtitle = "Analytics",
            },
        ];
    }

    public override IContent[] GetContent()
    {
        LoadContent(forceRefresh: false);
        return [_content];
    }

    private void LoadContent(bool forceRefresh)
    {
        if (forceRefresh)
        {
            _client.ClearCache();
        }

        _snapshot = _client.ReadSnapshotAsync().GetAwaiter().GetResult() ?? _snapshot;
        _content.DataJson = BuildDataJson();
    }

    private string BuildDataJson()
    {
        if (_snapshot?.Providers.Count > 0 != true)
        {
            return new JsonObject
            {
                ["errorMessage"] = "No local usage found.",
            }.ToJsonString();
        }

        var provider = _snapshot.Providers.FirstOrDefault(provider => provider.Id == "codex")
            ?? _snapshot.Providers[0];

        if (!string.IsNullOrWhiteSpace(provider.Error))
        {
            return new JsonObject
            {
                ["errorMessage"] = $"Local scan failed: {provider.Error}",
            }.ToJsonString();
        }

        var daily = CompleteDaily(provider.DailyCosts);
        var hourly = CompleteHours(provider.HourlyCosts);
        var chart = ChartForMode(provider, daily, hourly);
        var fields = DetailFieldsForMode(provider, chart);

        return new JsonObject
        {
            ["errorMessage"] = null,
            ["title"] = DetailTitleForMode(_mode),
            ["subtitle"] = provider.Subtitle ?? "",
            ["chartUrl"] = ChartImageUrl(chart),
            ["chartHeight"] = "160px",
            ["chartWidth"] = "520px",
            ["leftLabel1"] = fields.LeftLabel1,
            ["leftValue1"] = fields.LeftValue1,
            ["leftLabel2"] = fields.LeftLabel2,
            ["leftValue2"] = fields.LeftValue2,
            ["rightLabel1"] = fields.RightLabel1,
            ["rightValue1"] = fields.RightValue1,
            ["rightLabel2"] = fields.RightLabel2,
            ["rightValue2"] = fields.RightValue2,
        }.ToJsonString();
    }

    private static string TitleForMode(CodexToysDetailMode mode)
    {
        return mode switch
        {
            CodexToysDetailMode.TodayCost => "Today",
            CodexToysDetailMode.ThirtyDayCost => "30d",
            CodexToysDetailMode.Tokens => "Tokens",
            _ => "CodexToys",
        };
    }

    private static string DetailTitleForMode(CodexToysDetailMode mode)
    {
        return mode switch
        {
            CodexToysDetailMode.TodayCost => "Today Usage",
            CodexToysDetailMode.ThirtyDayCost => "30 Day Usage",
            CodexToysDetailMode.Tokens => "Token Usage",
            _ => "Codex Usage",
        };
    }

    private ChartDefinition ChartForMode(
        CodexToysProviderSnapshot provider,
        List<CodexToysDailyCostPoint> daily,
        List<CodexToysHourlyUsagePoint> hourly)
    {
        return _mode switch
        {
            CodexToysDetailMode.TodayCost => new ChartDefinition(
                "Today",
                "hourly cost",
                hourly.Select(point => new ChartPoint($"{point.Hour:00}", point.Cost)).ToList(),
                value => FormatUsd(value),
                "#22c55e"),
            CodexToysDetailMode.ThirtyDayCost => new ChartDefinition(
                "30d",
                "daily cost",
                daily.Select(point => new ChartPoint(point.Date[^5..], point.Cost)).ToList(),
                value => FormatUsd(value),
                "#38bdf8"),
            CodexToysDetailMode.Tokens => new ChartDefinition(
                "Tokens",
                "daily token usage",
                daily.Select(point => new ChartPoint(point.Date[^5..], point.Tokens)).ToList(),
                value => FormatCount((ulong)Math.Round(value)),
                "#a78bfa"),
            _ => new ChartDefinition(
                "CodexToys",
                "30d daily cost",
                daily.Select(point => new ChartPoint(point.Date[^5..], point.Cost)).ToList(),
                value => FormatUsd(value),
                "#38bdf8"),
        };
    }

    private DetailFields DetailFieldsForMode(CodexToysProviderSnapshot provider, ChartDefinition chart)
    {
        var model = provider.TopModel ?? "--";
        var peak = chart.Points.Count == 0 ? 0 : chart.Points.Max(point => point.Value);

        return _mode switch
        {
            CodexToysDetailMode.TodayCost => new DetailFields(
                "Today",
                FormatUsd(provider.TodayCost),
                "Tokens",
                FormatCount(provider.LatestTokens),
                "Model",
                model),
            CodexToysDetailMode.ThirtyDayCost => new DetailFields(
                "30d",
                FormatUsd(provider.ThirtyDayCost),
                "Tokens",
                FormatCount(provider.ThirtyDayTokens),
                "Model",
                model),
            CodexToysDetailMode.Tokens => new DetailFields(
                "Tokens",
                FormatCount(provider.ThirtyDayTokens),
                "Latest",
                FormatCount(provider.LatestTokens),
                "Model",
                model,
                "Peak",
                chart.FormatValue(peak)),
            _ => new DetailFields(
                provider.PrimaryLabel ?? "Session",
                provider.Primary is { } primary
                    ? $"{primary.UsedPercent:0}%"
                    : string.IsNullOrWhiteSpace(provider.StatusText) ? "--" : provider.StatusText,
                provider.SecondaryLabel ?? "Weekly",
                provider.Secondary is { } secondary ? $"{secondary.UsedPercent:0}%" : "--",
                "Model",
                model),
        };
    }

    private static string FormatCount(ulong? value)
    {
        return value is ulong count ? FormatCount(count) : "--";
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

    private static string FormatUsd(double? value)
    {
        return value is double amount ? FormatUsd(amount) : "--";
    }

    private static string FormatUsd(double value)
    {
        return $"${value:0.00}";
    }

    private static List<CodexToysHourlyUsagePoint> CompleteHours(IEnumerable<CodexToysHourlyUsagePoint> points)
    {
        var byHour = points.ToDictionary(point => point.Hour);
        var result = new List<CodexToysHourlyUsagePoint>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            result.Add(byHour.TryGetValue(hour, out var point)
                ? point
                : new CodexToysHourlyUsagePoint { Hour = hour });
        }

        return result;
    }

    private static List<CodexToysDailyCostPoint> CompleteDaily(IEnumerable<CodexToysDailyCostPoint> points)
    {
        var byDate = points.ToDictionary(point => point.Date, StringComparer.Ordinal);
        var today = DateTime.Now.Date;
        var result = new List<CodexToysDailyCostPoint>(30);
        for (var offset = 29; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            result.Add(byDate.TryGetValue(date, out var point)
                ? point
                : new CodexToysDailyCostPoint { Date = date });
        }

        return result;
    }

    private static string ChartImageUrl(ChartDefinition chart)
    {
        return "data:image/svg+xml;utf8," + Uri.EscapeDataString(BarChartSvg(chart));
    }

    private static string BarChartSvg(ChartDefinition chart)
    {
        const int width = 520;
        const int height = 160;
        const int chartWidth = 476;
        const int chartHeight = 112;
        const int chartLeft = 22;
        const int chartTop = 24;
        var max = Math.Max(chart.Points.Count == 0 ? 0 : chart.Points.Max(point => point.Value), 1);
        var barGap = chart.Points.Count > 24 ? 3 : 5;
        var barWidth = chart.Points.Count == 0
            ? chartWidth
            : Math.Max(4, (chartWidth - barGap * (chart.Points.Count - 1)) / chart.Points.Count);
        var axisColor = "#8a8a8a";
        var gridColor = "#8a8a8a";
        var textColor = "#f2f2f2";
        var textStrokeColor = "#1f1f1f";
        var mutedColor = "#d0d0d0";
        var sb = new StringBuilder();

        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"12\" y=\"16\" fill=\"{textColor}\" stroke=\"{textStrokeColor}\" stroke-width=\"2\" paint-order=\"stroke\" font-family=\"Segoe UI, Arial\" font-size=\"12\">{EscapeXml(chart.Title)}</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{width - 12}\" y=\"16\" fill=\"{mutedColor}\" stroke=\"{textStrokeColor}\" stroke-width=\"2\" paint-order=\"stroke\" font-family=\"Segoe UI, Arial\" font-size=\"11\" text-anchor=\"end\">peak {EscapeXml(chart.FormatValue(max))}</text>");

        for (var tick = 0; tick <= 4; tick++)
        {
            var y = chartTop + chartHeight - tick * chartHeight / 4.0;
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{chartLeft}\" y1=\"{y:0.#}\" x2=\"{chartLeft + chartWidth}\" y2=\"{y:0.#}\" stroke=\"{gridColor}\" stroke-width=\"1\" opacity=\"0.28\"/>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{chartLeft}\" y=\"{chartTop}\" width=\"{chartWidth}\" height=\"{chartHeight}\" fill=\"none\" stroke=\"{axisColor}\" stroke-width=\"1\"/>");

        var nonZeroCount = chart.Points.Count(point => point.Value > 0);
        var lastNonZeroIndex = chart.Points.FindLastIndex(point => point.Value > 0);
        for (var index = 0; index < chart.Points.Count; index++)
        {
            var point = chart.Points[index];
            var x = chartLeft + index * (barWidth + barGap);
            var barHeight = Math.Max(point.Value <= 0 ? 0 : 2, point.Value / max * chartHeight);
            var y = chartTop + chartHeight - barHeight;
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y:0.#}\" width=\"{barWidth}\" height=\"{barHeight:0.#}\" rx=\"2\" fill=\"{chart.Accent}\" opacity=\"0.92\"/>");

            var shouldLabelValue = point.Value > 0 &&
                (nonZeroCount <= 8 || point.Value == max || index == lastNonZeroIndex);
            if (shouldLabelValue)
            {
                var labelY = Math.Max(34, y - 4);
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{x + barWidth / 2.0:0.#}\" y=\"{labelY:0.#}\" fill=\"{textColor}\" stroke=\"{textStrokeColor}\" stroke-width=\"2\" paint-order=\"stroke\" font-family=\"Segoe UI, Arial\" font-size=\"9\" text-anchor=\"middle\">{EscapeXml(chart.FormatValue(point.Value))}</text>");
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{chartLeft}\" y=\"{height - 8}\" fill=\"{mutedColor}\" stroke=\"{textStrokeColor}\" stroke-width=\"2\" paint-order=\"stroke\" font-family=\"Segoe UI, Arial\" font-size=\"10\">{EscapeXml(chart.Points.FirstOrDefault().Label)}</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{chartLeft + chartWidth}\" y=\"{height - 8}\" fill=\"{mutedColor}\" stroke=\"{textStrokeColor}\" stroke-width=\"2\" paint-order=\"stroke\" font-family=\"Segoe UI, Arial\" font-size=\"10\" text-anchor=\"end\">{EscapeXml(chart.Points.LastOrDefault().Label)}</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string EscapeXml(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private sealed partial class RefreshStatusCommand : InvokableCommand
    {
        private readonly CodexToysStatusPage _page;

        public RefreshStatusCommand(CodexToysStatusPage page)
        {
            _page = page;
            Name = "Refresh";
            Icon = new IconInfo("\uE72C");
        }

        public override ICommandResult Invoke()
        {
            _page.LoadContent(forceRefresh: true);
            return CommandResult.KeepOpen();
        }
    }

    private sealed partial class OpenCodexSettingsCommand : InvokableCommand
    {
        private readonly OpenUrlCommand _openUrl = new(CodexSettingsUrl)
        {
            Result = CommandResult.KeepOpen(),
        };

        public OpenCodexSettingsCommand()
        {
            Name = "Open Codex Settings";
            Icon = new IconInfo("\uE774");
        }

        public override ICommandResult Invoke()
        {
            return _openUrl.Invoke();
        }
    }

    private sealed record ChartDefinition(
        string Title,
        string Subtitle,
        List<ChartPoint> Points,
        Func<double, string> FormatValue,
        string Accent);

    private readonly record struct DetailFields(
        string LeftLabel1,
        string LeftValue1,
        string RightLabel1,
        string RightValue1,
        string LeftLabel2,
        string LeftValue2,
        string RightLabel2 = "",
        string RightValue2 = "");

    private readonly record struct ChartPoint(string Label, double Value);

    private const string CardTemplate = """
    {
      "type": "AdaptiveCard",
      "body": [
        {
          "type": "Container",
          "$when": "${errorMessage != null}",
          "style": "warning",
          "items": [
            {
              "type": "TextBlock",
              "text": "${errorMessage}",
              "wrap": true,
              "size": "medium"
            }
          ]
        },
        {
          "type": "Container",
          "$when": "${errorMessage == null}",
          "items": [
            {
              "type": "Image",
              "url": "${chartUrl}",
              "height": "${chartHeight}",
              "width": "${chartWidth}",
              "horizontalAlignment": "center"
            },
            {
              "type": "ColumnSet",
              "spacing": "large",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${leftLabel1}",
                      "isSubtle": true,
                      "size": "medium"
                    },
                    {
                      "type": "TextBlock",
                      "text": "${leftValue1}",
                      "size": "extraLarge",
                      "weight": "bolder"
                    },
                    {
                      "type": "TextBlock",
                      "$when": "${leftLabel2 != ''}",
                      "text": "${leftLabel2}",
                      "isSubtle": true,
                      "spacing": "medium",
                      "size": "small"
                    },
                    {
                      "type": "TextBlock",
                      "$when": "${leftLabel2 != ''}",
                      "text": "${leftValue2}",
                      "size": "medium",
                      "weight": "bolder"
                    },
                    {
                      "type": "TextBlock",
                      "text": "${subtitle}",
                      "wrap": true,
                      "isSubtle": true,
                      "spacing": "large",
                      "size": "medium"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${rightLabel1}",
                      "isSubtle": true,
                      "horizontalAlignment": "right",
                      "size": "medium"
                    },
                    {
                      "type": "TextBlock",
                      "text": "${rightValue1}",
                      "size": "extraLarge",
                      "horizontalAlignment": "right",
                      "weight": "bolder"
                    },
                    {
                      "type": "TextBlock",
                      "$when": "${rightLabel2 != ''}",
                      "text": "${rightLabel2}",
                      "isSubtle": true,
                      "horizontalAlignment": "right",
                      "spacing": "medium",
                      "size": "small"
                    },
                    {
                      "type": "TextBlock",
                      "$when": "${rightLabel2 != ''}",
                      "text": "${rightValue2}",
                      "size": "medium",
                      "horizontalAlignment": "right",
                      "weight": "bolder"
                    }
                  ]
                }
              ]
            }
          ]
        }
      ],
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5"
    }
    """;
}

internal enum CodexToysDetailMode
{
    Overview,
    TodayCost,
    ThirtyDayCost,
    Tokens,
}
