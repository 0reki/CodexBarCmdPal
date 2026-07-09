using System.Text.Json.Serialization;

namespace CodexToys;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CodexToysStatusSnapshot))]
internal sealed partial class CodexToysJsonContext : JsonSerializerContext;
