using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Globalization;
using System.Net;
using System.Text;

namespace CodexBarCmdPal;

internal sealed partial class CodexToysStatusPage : ContentPage
{
    private static readonly IconInfo StatusIcon = new("\uE9D9");
    private readonly CodexToysStatusClient _client;
    private readonly CodexToysDetailMode _mode;
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
    }

    public override IContent[] GetContent()
    {
        _snapshot = _client.ReadSnapshotAsync().GetAwaiter().GetResult() ?? _snapshot;

        return [new MarkdownContent(BuildMarkdown())];
    }

    private string BuildMarkdown()
    {
        if (_snapshot?.Providers.Count > 0 != true)
        {
            return """
            # CodexToys

            No local usage found.
            """;
        }

        var provider = _snapshot.Providers.FirstOrDefault(provider => provider.Id == "codex")
            ?? _snapshot.Providers[0];

        if (!string.IsNullOrWhiteSpace(provider.Error))
        {
            return $"# CodexToys\n\nLocal scan failed: `{provider.Error}`";
        }

        var daily = CompleteDaily(provider.DailyCosts);
        var hourly = CompleteHours(provider.HourlyCosts);
        var markdown = new StringBuilder();
        markdown.AppendLine(ChartImage(ChartForMode(provider, daily, hourly)));
        return markdown.ToString();
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
                "USD",
                hourly.Select(point => new ChartPoint($"{point.Hour:00}", point.Cost)).ToList(),
                value => FormatUsd(value),
                "#22c55e",
                [
                    new Metric("Today", FormatUsd(provider.TodayCost)),
                    new Metric("Peak hour", FormatUsd(hourly.Count == 0 ? 0 : hourly.Max(point => point.Cost))),
                    new Metric("Tokens", FormatCount(provider.LatestTokens)),
                    new Metric("Model", provider.TopModel ?? "--"),
                ]),
            CodexToysDetailMode.ThirtyDayCost => new ChartDefinition(
                "30d",
                "daily cost",
                "USD",
                daily.Select(point => new ChartPoint(point.Date[^5..], point.Cost)).ToList(),
                value => FormatUsd(value),
                "#38bdf8",
                [
                    new Metric("30d cost", FormatUsd(provider.ThirtyDayCost)),
                    new Metric("Today", FormatUsd(provider.TodayCost)),
                    new Metric("30d tokens", FormatCount(provider.ThirtyDayTokens)),
                    new Metric("Model", provider.TopModel ?? "--"),
                ]),
            CodexToysDetailMode.Tokens => new ChartDefinition(
                "Tokens",
                "daily token usage",
                "tokens",
                daily.Select(point => new ChartPoint(point.Date[^5..], point.Tokens)).ToList(),
                value => FormatCount((ulong)Math.Round(value)),
                "#a78bfa",
                [
                    new Metric("30d tokens", FormatCount(provider.ThirtyDayTokens)),
                    new Metric("Today tokens", FormatCount(provider.LatestTokens)),
                    new Metric("30d cost", FormatUsd(provider.ThirtyDayCost)),
                    new Metric("Model", provider.TopModel ?? "--"),
                ]),
            _ => new ChartDefinition(
                "CodexToys",
                "30d daily cost",
                "USD",
                daily.Select(point => new ChartPoint(point.Date[^5..], point.Cost)).ToList(),
                value => FormatUsd(value),
                "#38bdf8",
                [
                    new Metric("Today", FormatUsd(provider.TodayCost)),
                    new Metric("30d cost", FormatUsd(provider.ThirtyDayCost)),
                    new Metric("30d tokens", FormatCount(provider.ThirtyDayTokens)),
                    new Metric("Latest tokens", FormatCount(provider.LatestTokens)),
                ]),
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

    private static string ChartImage(ChartDefinition chart)
    {
        var svg = BarChartSvg(chart);
        try
        {
            var chartDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexToys",
                "charts");
            Directory.CreateDirectory(chartDir);
            var chartPath = Path.Combine(chartDir, $"{SafeFileName(chart.Title)}.svg");
            File.WriteAllText(chartPath, svg, Encoding.UTF8);
            var uri = new Uri(chartPath).AbsoluteUri;
            return $"![{WebUtility.HtmlEncode(chart.Title)}]({uri})";
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Failed to write chart image: {ex.GetType().Name}: {ex.Message}");
            return $"`chart unavailable: {ex.Message}`";
        }
    }

    private static string BarChartSvg(ChartDefinition chart)
    {
        const int width = 760;
        const int height = 470;
        const int chartWidth = 520;
        const int chartHeight = 170;
        const int chartLeft = (width - chartWidth) / 2;
        const int chartTop = 64;
        const int metricTop = 310;
        const int metricLeft = 72;
        const int metricRight = width - 72;
        var max = Math.Max(chart.Points.Count == 0 ? 0 : chart.Points.Max(point => point.Value), 1);
        var barGap = chart.Points.Count > 24 ? 4 : 7;
        var barWidth = chart.Points.Count == 0
            ? chartWidth
            : Math.Max(4, (chartWidth - barGap * (chart.Points.Count - 1)) / chart.Points.Count);
        var axisColor = "#475569";
        var gridColor = "#273241";
        var textColor = "#cbd5e1";
        var mutedColor = "#94a3b8";
        var sb = new StringBuilder();

        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#1f1f1f\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"24\" y=\"28\" fill=\"{textColor}\" font-family=\"Segoe UI, Arial\" font-size=\"14\">{EscapeXml(chart.Title)}</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{width / 2}\" y=\"44\" fill=\"{mutedColor}\" font-family=\"Segoe UI, Arial\" font-size=\"12\" text-anchor=\"middle\">{EscapeXml(chart.Subtitle)}</text>");

        for (var tick = 0; tick <= 4; tick++)
        {
            var y = chartTop + chartHeight - tick * chartHeight / 4.0;
            var value = max * tick / 4.0;
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{chartLeft}\" y1=\"{y:0.#}\" x2=\"{chartLeft + chartWidth}\" y2=\"{y:0.#}\" stroke=\"{gridColor}\" stroke-width=\"1\"/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{chartLeft - 10}\" y=\"{y + 4:0.#}\" fill=\"{mutedColor}\" font-family=\"Segoe UI, Arial\" font-size=\"10\" text-anchor=\"end\">{EscapeXml(chart.FormatValue(value))}</text>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{chartLeft}\" y=\"{chartTop}\" width=\"{chartWidth}\" height=\"{chartHeight}\" fill=\"none\" stroke=\"{axisColor}\" stroke-width=\"1\"/>");

        for (var index = 0; index < chart.Points.Count; index++)
        {
            var point = chart.Points[index];
            var x = chartLeft + index * (barWidth + barGap);
            var barHeight = Math.Max(point.Value <= 0 ? 0 : 2, point.Value / max * chartHeight);
            var y = chartTop + chartHeight - barHeight;
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x}\" y=\"{y:0.#}\" width=\"{barWidth}\" height=\"{barHeight:0.#}\" rx=\"3\" fill=\"{chart.Accent}\" opacity=\"0.92\"/>");

            var shouldLabel = chart.Points.Count <= 24 || index == 0 || index == chart.Points.Count - 1 || index % 5 == 4;
            if (shouldLabel)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{x + barWidth / 2.0:0.#}\" y=\"{chartTop + chartHeight + 20}\" fill=\"{mutedColor}\" font-family=\"Segoe UI, Arial\" font-size=\"10\" text-anchor=\"middle\">{EscapeXml(point.Label)}</text>");
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{chartLeft + chartWidth}\" y=\"{chartTop - 12}\" fill=\"{mutedColor}\" font-family=\"Segoe UI, Arial\" font-size=\"11\" text-anchor=\"end\">peak {EscapeXml(chart.FormatValue(max))}</text>");
        for (var index = 0; index < chart.Metrics.Count; index++)
        {
            var metric = chart.Metrics[index];
            var isRight = index % 2 == 1;
            var x = isRight ? metricRight : metricLeft;
            var y = metricTop + index / 2 * 76;
            var anchor = isRight ? "end" : "start";
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y}\" fill=\"{textColor}\" font-family=\"Segoe UI, Arial\" font-size=\"13\" text-anchor=\"{anchor}\">{EscapeXml(metric.Label)}</text>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{x}\" y=\"{y + 31}\" fill=\"#f8fafc\" font-family=\"Segoe UI, Arial\" font-size=\"24\" font-weight=\"600\" text-anchor=\"{anchor}\">{EscapeXml(metric.Value)}</text>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string EscapeXml(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        return new string(chars).Trim();
    }

    private sealed record ChartDefinition(
        string Title,
        string Subtitle,
        string Unit,
        List<ChartPoint> Points,
        Func<double, string> FormatValue,
        string Accent,
        List<Metric> Metrics);

    private readonly record struct Metric(string Label, string Value);

    private readonly record struct ChartPoint(string Label, double Value);
}

internal enum CodexToysDetailMode
{
    Overview,
    TodayCost,
    ThirtyDayCost,
    Tokens,
}
