using System.Text.Json.Serialization;

namespace CodexToys;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CodexToysStatusSnapshot))]
[JsonSerializable(typeof(CodexUsageCache))]
internal sealed partial class CodexToysJsonContext : JsonSerializerContext;
