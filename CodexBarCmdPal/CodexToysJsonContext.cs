using System.Text.Json.Serialization;

namespace CodexBarCmdPal;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CodexToysStatusSnapshot))]
internal sealed partial class CodexToysJsonContext : JsonSerializerContext;
