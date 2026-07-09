namespace CodexToys;

internal static class CodexPricing
{
    private const ulong LongContextThreshold = 272_000;
    private readonly record struct Price(double Input, double CachedInput, double? LongContextInput, double Output);

    private static readonly Dictionary<string, Price> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.6-sol"] = PerMillion(5.00, 0.50, 6.25, 30.00),
        ["gpt-5.6-terra"] = PerMillion(2.50, 0.25, 3.125, 15.00),
        ["gpt-5.6-luna"] = PerMillion(1.00, 0.10, 1.25, 6.00),
        ["gpt-5.5"] = PerMillion(5.00, 0.50, null, 30.00),
        ["gpt-5.5-pro"] = PerMillion(30.00, 30.00, null, 180.00),
        ["gpt-5.4"] = PerMillion(2.50, 0.25, null, 15.00),
        ["gpt-5.4-mini"] = PerMillion(0.75, 0.075, null, 4.50),
        ["gpt-5.4-nano"] = PerMillion(0.20, 0.02, null, 1.25),
        ["gpt-5.4-pro"] = PerMillion(30.00, 30.00, null, 180.00),
        ["gpt-5.2"] = PerMillion(1.75, 0.175, null, 14.00),
        ["gpt-5.2-pro"] = PerMillion(21.00, 21.00, null, 168.00),
        ["gpt-5.1"] = PerMillion(1.25, 0.125, null, 10.00),
        ["gpt-5"] = PerMillion(1.25, 0.125, null, 10.00),
        ["gpt-5-mini"] = PerMillion(0.25, 0.025, null, 2.00),
        ["gpt-5-nano"] = PerMillion(0.05, 0.005, null, 0.40),
        ["gpt-5-pro"] = PerMillion(15.00, 15.00, null, 120.00),
    };

    public static double CostUsd(string model, ulong inputTokens, ulong cachedInputTokens, ulong outputTokens)
    {
        var key = NormalizeModel(model);
        if (!Prices.TryGetValue(key, out var price))
        {
            price = FallbackPrice(model);
        }

        var cached = Math.Min(cachedInputTokens, inputTokens);
        var nonCached = inputTokens - cached;
        var inputPrice = inputTokens >= LongContextThreshold && price.LongContextInput is double longInput
            ? longInput
            : price.Input;
        return nonCached * inputPrice + cached * price.CachedInput + outputTokens * price.Output;
    }

    private static Price FallbackPrice(string model)
    {
        var lower = model.ToLowerInvariant();
        return lower switch
        {
            var value when value.Contains("gpt-5.6-sol", StringComparison.Ordinal) => PerMillion(5.00, 0.50, 6.25, 30.00),
            var value when value.Contains("gpt-5.6-terra", StringComparison.Ordinal) => PerMillion(2.50, 0.25, 3.125, 15.00),
            var value when value.Contains("gpt-5.6-luna", StringComparison.Ordinal) => PerMillion(1.00, 0.10, 1.25, 6.00),
            var value when value.Contains("gpt-5.5-pro", StringComparison.Ordinal) => PerMillion(30.00, 30.00, null, 180.00),
            var value when value.Contains("gpt-5.5", StringComparison.Ordinal) => PerMillion(5.00, 0.50, null, 30.00),
            var value when value.Contains("gpt-5.4-pro", StringComparison.Ordinal) => PerMillion(30.00, 30.00, null, 180.00),
            var value when value.Contains("gpt-5.4-mini", StringComparison.Ordinal) => PerMillion(0.75, 0.075, null, 4.50),
            var value when value.Contains("gpt-5.4-nano", StringComparison.Ordinal) => PerMillion(0.20, 0.02, null, 1.25),
            var value when value.Contains("gpt-5.4", StringComparison.Ordinal) => PerMillion(2.50, 0.25, null, 15.00),
            var value when value.Contains("gpt-5.3", StringComparison.Ordinal) => PerMillion(1.75, 0.175, null, 14.00),
            var value when value.Contains("gpt-5.2-pro", StringComparison.Ordinal) => PerMillion(21.00, 21.00, null, 168.00),
            var value when value.Contains("gpt-5.2", StringComparison.Ordinal) => PerMillion(1.75, 0.175, null, 14.00),
            var value when value.Contains("gpt-5.1", StringComparison.Ordinal) => PerMillion(1.25, 0.125, null, 10.00),
            var value when value.Contains("gpt-5-pro", StringComparison.Ordinal) => PerMillion(15.00, 15.00, null, 120.00),
            var value when value.Contains("gpt-5-mini", StringComparison.Ordinal) => PerMillion(0.25, 0.025, null, 2.00),
            var value when value.Contains("gpt-5-nano", StringComparison.Ordinal) => PerMillion(0.05, 0.005, null, 0.40),
            var value when value.Contains("gpt-5", StringComparison.Ordinal) => PerMillion(1.25, 0.125, null, 10.00),
            _ => PerMillion(1.25, 0.125, null, 10.00),
        };
    }

    private static Price PerMillion(double input, double cachedInput, double output)
    {
        return PerMillion(input, cachedInput, null, output);
    }

    private static Price PerMillion(double input, double cachedInput, double? longContextInput, double output)
    {
        return new Price(
            input / 1_000_000.0,
            cachedInput / 1_000_000.0,
            longContextInput / 1_000_000.0,
            output / 1_000_000.0);
    }

    private static string NormalizeModel(string raw)
    {
        var model = raw.Trim();
        if (model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            model = model["openai/".Length..];
        }

        var codexIndex = model.IndexOf("-codex", StringComparison.OrdinalIgnoreCase);
        if (codexIndex > 0)
        {
            var baseModel = model[..codexIndex];
            if (Prices.ContainsKey(baseModel))
            {
                return baseModel;
            }

            var versionFallback = baseModel switch
            {
                "gpt-5.3" => "gpt-5.2",
                _ => null,
            };
            if (versionFallback is not null && Prices.ContainsKey(versionFallback))
            {
                return versionFallback;
            }
        }

        if (model.Length > 11 && model[^11] == '-' &&
            char.IsDigit(model[^10]) && char.IsDigit(model[^9]) &&
            char.IsDigit(model[^8]) && char.IsDigit(model[^7]) &&
            model[^6] == '-' &&
            char.IsDigit(model[^5]) && char.IsDigit(model[^4]) &&
            model[^3] == '-' &&
            char.IsDigit(model[^2]) && char.IsDigit(model[^1]))
        {
            var withoutDate = model[..^11];
            if (Prices.ContainsKey(withoutDate))
            {
                return withoutDate;
            }
        }

        return model;
    }
}
