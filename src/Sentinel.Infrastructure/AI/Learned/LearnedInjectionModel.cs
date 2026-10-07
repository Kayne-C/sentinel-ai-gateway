using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.Infrastructure.AI.Learned;

/// <summary>
/// A logistic-regression model over sentence embeddings, trained by <c>eval/train/train_injection_classifier.py</c> and
/// embedded in this assembly together with its provenance and held-out evaluation. The weights belong to one embedding
/// model: scoring vectors of another model would silently produce nonsense, so the model records which one it expects.
/// </summary>
internal sealed class LearnedInjectionModel
{
    private const string ResourceName = "Sentinel.Infrastructure.AI.Learned.injection-model.json";

    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; init; } = string.Empty;

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; init; }

    [JsonPropertyName("maxCharacters")]
    public int MaxCharacters { get; init; }

    [JsonPropertyName("threshold")]
    public double Threshold { get; init; }

    [JsonPropertyName("bias")]
    public double Bias { get; init; }

    [JsonPropertyName("weights")]
    public double[] Weights { get; init; } = [];

    public static LearnedInjectionModel Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} was not found.");
        var model = JsonSerializer.Deserialize<LearnedInjectionModel>(stream)
            ?? throw new InvalidOperationException("The injection model resource is empty.");
        if (model.Weights.Length != model.Dimensions || model.Dimensions <= 0 || string.IsNullOrWhiteSpace(model.EmbeddingModel))
        {
            throw new InvalidOperationException("The injection model resource is inconsistent.");
        }

        return model;
    }

    /// <summary>Probability that the embedded text is an injection attempt (unit-normalises the vector first).</summary>
    public double Score(ReadOnlySpan<float> embedding)
    {
        if (embedding.Length != Dimensions)
        {
            throw new ArgumentException($"Expected {Dimensions} dimensions but got {embedding.Length}.", nameof(embedding));
        }

        double norm = 0;
        foreach (var value in embedding)
        {
            norm += (double)value * value;
        }

        norm = Math.Sqrt(norm);
        if (norm < 1e-12)
        {
            return 0;
        }

        var sum = Bias;
        for (var i = 0; i < Weights.Length; i++)
        {
            sum += Weights[i] * embedding[i] / norm;
        }

        return 1 / (1 + Math.Exp(-sum));
    }
}
