using System.Text.Json.Serialization;

namespace CodexBarCmdPal;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CodexBarStatusSnapshot))]
internal sealed partial class CodexBarJsonContext : JsonSerializerContext;
