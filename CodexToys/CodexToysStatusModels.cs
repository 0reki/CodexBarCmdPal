using System.Text.Json.Serialization;

namespace CodexToys;

internal sealed class CodexToysStatusSnapshot
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("providers")]
    public List<CodexToysProviderSnapshot> Providers { get; set; } = [];
}

internal sealed class CodexToysProviderSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("statusText")]
    public string StatusText { get; set; } = "";

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("primaryLabel")]
    public string? PrimaryLabel { get; set; }

    [JsonPropertyName("primary")]
    public CodexToysRateWindowSnapshot? Primary { get; set; }

    [JsonPropertyName("secondaryLabel")]
    public string? SecondaryLabel { get; set; }

    [JsonPropertyName("secondary")]
    public CodexToysRateWindowSnapshot? Secondary { get; set; }

    [JsonPropertyName("todayCost")]
    public double? TodayCost { get; set; }

    [JsonPropertyName("thirtyDayCost")]
    public double? ThirtyDayCost { get; set; }

    [JsonPropertyName("latestTokens")]
    public ulong? LatestTokens { get; set; }

    [JsonPropertyName("thirtyDayTokens")]
    public ulong? ThirtyDayTokens { get; set; }

    [JsonPropertyName("topModel")]
    public string? TopModel { get; set; }

    [JsonPropertyName("todayTopModel")]
    public string? TodayTopModel { get; set; }

    [JsonPropertyName("dailyCosts")]
    public List<CodexToysDailyCostPoint> DailyCosts { get; set; } = [];

    [JsonPropertyName("hourlyCosts")]
    public List<CodexToysHourlyUsagePoint> HourlyCosts { get; set; } = [];

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

internal sealed class CodexToysRateWindowSnapshot
{
    [JsonPropertyName("usedPercent")]
    public double UsedPercent { get; set; }

    [JsonPropertyName("remainingPercent")]
    public double RemainingPercent { get; set; }

    [JsonPropertyName("windowMinutes")]
    public uint? WindowMinutes { get; set; }

    [JsonPropertyName("resetsAt")]
    public string? ResetsAt { get; set; }

    [JsonPropertyName("resetDescription")]
    public string? ResetDescription { get; set; }

    [JsonPropertyName("isExhausted")]
    public bool IsExhausted { get; set; }
}

internal sealed class CodexToysDailyCostPoint
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("cost")]
    public double Cost { get; set; }

    [JsonPropertyName("tokens")]
    public ulong Tokens { get; set; }
}

internal sealed class CodexToysHourlyUsagePoint
{
    [JsonPropertyName("hour")]
    public int Hour { get; set; }

    [JsonPropertyName("cost")]
    public double Cost { get; set; }

    [JsonPropertyName("tokens")]
    public ulong Tokens { get; set; }
}
