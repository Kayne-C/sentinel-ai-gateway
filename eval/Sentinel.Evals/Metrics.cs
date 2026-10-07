using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.Evals;

public sealed record Confusion(int TruePositives, int FalsePositives, int FalseNegatives)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 1 : (double)TruePositives / (TruePositives + FalsePositives);

    public double Recall => TruePositives + FalseNegatives == 0 ? 1 : (double)TruePositives / (TruePositives + FalseNegatives);

    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    public static Confusion operator +(Confusion a, Confusion b) =>
        new(a.TruePositives + b.TruePositives, a.FalsePositives + b.FalsePositives, a.FalseNegatives + b.FalseNegatives);
}

public static class Report
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string OutputDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "eval", "out");

    public static void Save(string name, object result)
    {
        Directory.CreateDirectory(OutputDirectory);
        var path = Path.Combine(OutputDirectory, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"-> {path}");
    }

    public static string Pct(double value) => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return double.NaN;
        }

        var rank = p * (sortedAscending.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sortedAscending[lower] + (sortedAscending[upper] - sortedAscending[lower]) * (rank - lower);
    }

    /// <summary>Wilson score interval (95%): honest uncertainty for rates measured on a finite sample.</summary>
    public static (double Low, double High) Wilson(int successes, int total)
    {
        if (total == 0)
        {
            return (0, 1);
        }

        const double z = 1.96;
        var p = (double)successes / total;
        var denominator = 1 + z * z / total;
        var centre = (p + z * z / (2 * total)) / denominator;
        var margin = z * Math.Sqrt(p * (1 - p) / total + z * z / (4.0 * total * total)) / denominator;
        return (Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }

    public static string Interval(int successes, int total)
    {
        var (low, high) = Wilson(successes, total);
        return $"{Pct((double)successes / Math.Max(1, total))} [95% CI {Pct(low)}–{Pct(high)}], n={total}";
    }
}
