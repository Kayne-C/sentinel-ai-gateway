using Sentinel.Evals;

var command = args.Length > 0 ? args[0] : "help";
var options = args.Skip(1).Where(a => a.StartsWith('-')).Select(a => a.Split('=', 2)).ToDictionary(p => p[0].TrimStart('-'), p => p.Length == 2 ? p[1] : "true", StringComparer.OrdinalIgnoreCase);
if (options.TryGetValue("out", out var outputDirectory))
{
    Report.OutputDirectory = outputDirectory;
}

switch (command)
{
    case "pii":
        var (precision, recall) = PiiEval.Run(
            positives: int.Parse(options.GetValueOrDefault("positives", "2000")),
            negatives: int.Parse(options.GetValueOrDefault("negatives", "1000")),
            seed: int.Parse(options.GetValueOrDefault("seed", "20261007")));
        if (precision < Gate(options, "min-precision", 0) || recall < Gate(options, "min-recall", 0))
        {
            Console.Error.WriteLine("PII quality gate failed.");
            return 2;
        }

        break;
    case "injection":
        var (injectionRecall, falsePositiveRate) = await InjectionEval.RunAsync(
            threshold: double.Parse(options.GetValueOrDefault("threshold", "0.7"), System.Globalization.CultureInfo.InvariantCulture),
            useDeepset: !options.ContainsKey("no-deepset"));
        if (injectionRecall < Gate(options, "min-recall", 0) || falsePositiveRate > Gate(options, "max-fpr", 1))
        {
            Console.Error.WriteLine("Injection quality gate failed.");
            return 2;
        }

        break;
    case "redteam":
        await RedTeamEval.RunAsync(
            options.GetValueOrDefault("gateway", "http://localhost:5100"),
            options.GetValueOrDefault("corpus"),
            int.Parse(options.GetValueOrDefault("max", "100000")),
            options.GetValueOrDefault("name"));
        break;
    case "cache":
        await CacheEval.RunAsync(
            options.GetValueOrDefault("gateway", "http://localhost:5100"),
            options.GetValueOrDefault("ollama", "http://localhost:11434"),
            options.GetValueOrDefault("corpus"),
            endToEnd: !options.ContainsKey("no-e2e"));
        break;
    case "overhead":
        await OverheadEval.RunAsync(
            options.GetValueOrDefault("gateway", "http://localhost:5100"),
            options.GetValueOrDefault("ollama", "http://localhost:11434"),
            int.Parse(options.GetValueOrDefault("iterations", "40")));
        break;
    default:
        Console.WriteLine("usage: Sentinel.Evals <pii|injection|redteam|cache|overhead> [--key=value ...]");
        return 1;
}

return 0;

static double Gate(Dictionary<string, string> options, string name, double fallback) =>
    options.TryGetValue(name, out var value) ? double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
