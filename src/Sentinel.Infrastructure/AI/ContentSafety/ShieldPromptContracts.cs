using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.Infrastructure.AI.ContentSafety;

/// <summary>Request body of <c>text:shieldPrompt</c> (api-version 2024-09-01).</summary>
internal sealed record ShieldPromptRequest(string UserPrompt, IReadOnlyList<string> Documents);

internal sealed record ShieldPromptResponse(
    ShieldPromptAnalysis? UserPromptAnalysis,
    IReadOnlyList<ShieldPromptAnalysis>? DocumentsAnalysis);

internal sealed record ShieldPromptAnalysis(bool AttackDetected);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ShieldPromptRequest))]
[JsonSerializable(typeof(ShieldPromptResponse))]
internal sealed partial class ShieldPromptJsonContext : JsonSerializerContext;
