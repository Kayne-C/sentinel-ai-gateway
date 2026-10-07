using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Sentinel.Evals;

/// <summary>
/// What the gateway adds to a model call: the same tiny non-streamed completion sent straight to Ollama and through the
/// gateway's OpenAI-compatible proxy (authentication, PII redaction, injection detection incl. the learned classifier,
/// budget reservation, routing, forwarding, output guard, audit entry), interleaved so both see the same machine load.
/// </summary>
internal static class OverheadEval
{
    public static async Task RunAsync(string gatewayUrl, string ollamaUrl, int iterations)
    {
        using var direct = new HttpClient { BaseAddress = new Uri(ollamaUrl), Timeout = TimeSpan.FromMinutes(5) };
        using var gateway = new GatewayClient(gatewayUrl);
        await gateway.TokenAsync("reporting-app");

        var prompt = "Tek kelimeyle yanıtla: Türkiye'nin başkenti neresidir?";
        var directBody = new { model = "qwen2.5:0.5b", messages = new[] { new { role = "user", content = prompt } }, max_tokens = 8, temperature = 0 };
        var gatewayBody = new { model = "fast", messages = new[] { new { role = "user", content = prompt } }, max_tokens = 8, temperature = 0 };

        // Warm both paths (model load, JIT, connections).
        for (var i = 0; i < 3; i++)
        {
            await direct.PostAsJsonAsync("/v1/chat/completions", directBody);
            await gateway.PostAsync("reporting-app", "/v1/chat/completions", gatewayBody);
        }

        var directMs = new List<double>();
        var gatewayMs = new List<double>();
        for (var i = 0; i < iterations; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var response = await direct.PostAsJsonAsync("/v1/chat/completions", directBody))
            {
                response.EnsureSuccessStatusCode();
                await response.Content.ReadAsStringAsync();
            }

            directMs.Add(watch.Elapsed.TotalMilliseconds);

            var (status, _, milliseconds) = await gateway.PostAsync("reporting-app", "/v1/chat/completions", gatewayBody);
            if (status != 200)
            {
                throw new InvalidOperationException($"The gateway answered {status}.");
            }

            gatewayMs.Add(milliseconds);
        }

        directMs.Sort();
        gatewayMs.Sort();
        var overheadMedian = Report.Percentile(gatewayMs, 0.5) - Report.Percentile(directMs, 0.5);

        Console.WriteLine($"Proxy overhead, {iterations} interleaved non-streamed completions (8 output tokens, qwen2.5:0.5b on CPU)");
        Console.WriteLine($"  direct to Ollama : p50 {Report.Percentile(directMs, 0.5):0.0} ms, p95 {Report.Percentile(directMs, 0.95):0.0} ms");
        Console.WriteLine($"  through gateway  : p50 {Report.Percentile(gatewayMs, 0.5):0.0} ms, p95 {Report.Percentile(gatewayMs, 0.95):0.0} ms");
        Console.WriteLine($"  overhead (difference of medians): {overheadMedian:0.0} ms");

        Report.Save("overhead", new
        {
            iterations,
            direct = new { p50 = Report.Percentile(directMs, 0.5), p95 = Report.Percentile(directMs, 0.95) },
            gateway = new { p50 = Report.Percentile(gatewayMs, 0.5), p95 = Report.Percentile(gatewayMs, 0.95) },
            overheadMedianMs = overheadMedian,
        });
    }
}
