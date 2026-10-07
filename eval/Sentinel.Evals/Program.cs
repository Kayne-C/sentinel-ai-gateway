using Sentinel.Evals;

var command = args.Length > 0 ? args[0] : "help";
var options = args.Skip(1).Select(a => a.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].TrimStart('-'), p => p[1], StringComparer.OrdinalIgnoreCase);
if (options.TryGetValue("out", out var outputDirectory))
{
    Report.OutputDirectory = outputDirectory;
}

switch (command)
{
    case "pii":
        PiiEval.Run(
            positives: int.Parse(options.GetValueOrDefault("positives", "2000")),
            negatives: int.Parse(options.GetValueOrDefault("negatives", "1000")),
            seed: int.Parse(options.GetValueOrDefault("seed", "20261007")));
        break;
    case "injection":
        await InjectionEval.RunAsync(
            threshold: double.Parse(options.GetValueOrDefault("threshold", "0.7"), System.Globalization.CultureInfo.InvariantCulture),
            useDeepset: !options.ContainsKey("no-deepset"));
        break;
    default:
        Console.WriteLine("usage: Sentinel.Evals <pii|injection|redteam|cache|overhead> [--key=value ...]");
        return 1;
}

return 0;
