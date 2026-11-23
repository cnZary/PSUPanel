using System.Text.Json.Serialization;

namespace PSUPanel.Shared;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MonitorSnapshot))]
[JsonSerializable(typeof(AppSettings))]
public partial class MonitorJsonContext : JsonSerializerContext;
