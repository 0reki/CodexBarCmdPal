namespace CodexBarCmdPal;

internal static class CodexPricing
{
    private readonly record struct Price(double Input, double CachedInput, double Output);

    private static readonly Dictionary<string, Price> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5"] = new(1.25e-6, 1.25e-7, 1.0e-5),
        ["gpt-5-codex"] = new(1.25e-6, 1.25e-7, 1.0e-5),
        ["gpt-5-mini"] = new(2.5e-7, 2.5e-8, 2.0e-6),
        ["gpt-5-nano"] = new(5.0e-8, 5.0e-9, 4.0e-7),
        ["gpt-5-pro"] = new(1.5e-5, 1.5e-5, 1.2e-4),
        ["gpt-5.1"] = new(1.25e-6, 1.25e-7, 1.0e-5),
        ["gpt-5.1-codex"] = new(1.25e-6, 1.25e-7, 1.0e-5),
        ["gpt-5.1-codex-max"] = new(1.25e-6, 1.25e-7, 1.0e-5),
        ["gpt-5.1-codex-mini"] = new(2.5e-7, 2.5e-8, 2.0e-6),
        ["gpt-5.2"] = new(1.75e-6, 1.75e-7, 1.4e-5),
        ["gpt-5.2-codex"] = new(1.75e-6, 1.75e-7, 1.4e-5),
        ["gpt-5.2-pro"] = new(2.1e-5, 2.1e-5, 1.68e-4),
        ["gpt-5.3-codex"] = new(1.75e-6, 1.75e-7, 1.4e-5),
        ["gpt-5.3-codex-spark"] = new(0, 0, 0),
        ["gpt-5.4"] = new(2.5e-6, 2.5e-7, 1.5e-5),
        ["gpt-5.4-codex"] = new(2.5e-6, 2.5e-7, 1.5e-5),
        ["gpt-5.4-mini"] = new(7.5e-7, 7.5e-8, 4.5e-6),
        ["gpt-5.4-mini-codex"] = new(7.5e-7, 7.5e-8, 4.5e-6),
        ["gpt-5.4-nano"] = new(2.0e-7, 2.0e-8, 1.25e-6),
        ["gpt-5.4-nano-codex"] = new(2.0e-7, 2.0e-8, 1.25e-6),
        ["gpt-5.4-pro"] = new(3.0e-5, 3.0e-5, 1.8e-4),
        ["gpt-5.5"] = new(5.0e-6, 5.0e-7, 3.0e-5),
        ["gpt-5.5-pro"] = new(3.0e-5, 3.0e-5, 1.8e-4),
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
        return nonCached * price.Input + cached * price.CachedInput + outputTokens * price.Output;
    }

    private static Price FallbackPrice(string model)
    {
        var lower = model.ToLowerInvariant();
        return lower switch
        {
            var value when value.Contains("gpt-4o-mini", StringComparison.Ordinal) => PerMillion(0.15, 0.075, 0.60),
            var value when value.Contains("gpt-4o", StringComparison.Ordinal) => PerMillion(2.50, 1.25, 10.00),
            var value when value.Contains("gpt-4-turbo", StringComparison.Ordinal) => PerMillion(10.00, 5.00, 30.00),
            var value when value.Contains("gpt-4", StringComparison.Ordinal) => PerMillion(30.00, 15.00, 60.00),
            var value when value.Contains("o1-mini", StringComparison.Ordinal) => PerMillion(3.00, 1.50, 12.00),
            var value when value.Contains("o1", StringComparison.Ordinal) => PerMillion(15.00, 7.50, 60.00),
            _ => PerMillion(2.50, 1.25, 10.00),
        };
    }

    private static Price PerMillion(double input, double cachedInput, double output)
    {
        return new Price(input / 1_000_000.0, cachedInput / 1_000_000.0, output / 1_000_000.0);
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
