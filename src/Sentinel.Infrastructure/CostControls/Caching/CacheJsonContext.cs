using System.Text.Json;
using System.Text.Json.Serialization;
using Sentinel.Application.Abstractions;

namespace Sentinel.Infrastructure.CostControls.Caching;

/// <summary>Payload format of cached answers: source-generated (no reflection), camelCase, stable across instances.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CachedAnswer))]
internal sealed partial class CacheJsonContext : JsonSerializerContext;
