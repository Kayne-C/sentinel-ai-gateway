using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Infrastructure.CostControls;
using Sentinel.Infrastructure.CostControls.Routing;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

public sealed class ModelRouterTests
{
    private static readonly RoutingOptions Defaults = new();

    [Theory]
    [InlineData("What is the capital of France?")]
    [InlineData("Translate 'good morning' to German.")]
    [InlineData("Türkiye'nin başkenti neresidir?")]
    [InlineData("Yarın İstanbul'da hava nasıl olacak?")]
    public void Simple_prompts_go_to_the_fast_tier(string prompt)
    {
        var route = Route(prompt);

        Assert.Equal("fast", route.Tier);
        Assert.Equal("gpt-4.1-mini", route.Model);
        Assert.True(route.ComplexityScore < Defaults.ReasoningThreshold, string.Join("; ", route.Reasons));
    }

    [Theory]
    [InlineData("Compare PostgreSQL and SQL Server for OLTP workloads and list the pros and cons, step by step.")]
    [InlineData("Why does our churn rise in Q3? Analyze the trade-offs of the retention strategy.")]
    [InlineData("Design a migration plan from the monolith to microservices.")]
    [InlineData("Mikroservis ve monolit mimarilerini avantaj ve dezavantajlarıyla karşılaştır.")]
    [InlineData("Satış düşüşünün nedenini adım adım analiz et.")]
    [InlineData("Bu stratejiyi değerlendir ve neden başarısız olabileceğini açıkla.")]
    [InlineData("MIKROSERVIS VE MONOLITI KARŞILAŞTIR, ADIM ADIM DEĞERLENDİR.")]
    [InlineData("mikroservis ve monoliti karsilastir, adim adim degerlendir")]
    public void Reasoning_heavy_prompts_go_to_the_reasoning_tier(string prompt)
    {
        var route = Route(prompt);

        Assert.Equal("reasoning", route.Tier);
        Assert.Equal("o4-mini", route.Model);
        Assert.True(route.ComplexityScore >= Defaults.ReasoningThreshold, string.Join("; ", route.Reasons));
    }

    [Fact]
    public void Turkish_and_english_keywords_map_to_the_same_concepts()
    {
        var english = Route("Compare them step by step and evaluate the pros and cons.");
        var turkish = Route("Bunları adım adım karşılaştır, avantaj ve dezavantajları değerlendir.");

        Assert.Equal(english.ComplexityScore, turkish.ComplexityScore);
        Assert.Contains(english.Reasons, r => r.StartsWith("reasoning keywords: compare, evaluate, step-by-step, pros-and-cons", StringComparison.Ordinal));
        Assert.Contains(turkish.Reasons, r => r.StartsWith("reasoning keywords: compare, evaluate, step-by-step, pros-and-cons", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_prompt_alone_stays_on_the_fast_tier_but_tips_a_reasoning_question_over()
    {
        var longAlone = Route("Summarise the text.", promptTokens: 900);
        var longWithKeyword = Route("Why did revenue fall?", promptTokens: 900);

        Assert.Equal("fast", longAlone.Tier);
        Assert.Equal(0.30, longAlone.ComplexityScore, 3);
        Assert.Contains(longAlone.Reasons, r => r.StartsWith("long prompt: 900 tokens >= 600", StringComparison.Ordinal));
        Assert.Equal("reasoning", longWithKeyword.Tier);
        Assert.Equal(0.65, longWithKeyword.ComplexityScore, 3);
    }

    [Fact]
    public void Prompt_and_context_size_count_half_from_half_their_thresholds()
    {
        Assert.Equal(0.15, Route("Summarise.", promptTokens: 300).ComplexityScore, 3);
        Assert.Equal(0.00, Route("Summarise.", promptTokens: 299).ComplexityScore, 3);
        Assert.Equal(0.10, Route("Summarise.", contextTokens: 750).ComplexityScore, 3);
        Assert.Equal(0.20, Route("Summarise.", contextTokens: 1500).ComplexityScore, 3);
        Assert.Equal(0.50, Route("Summarise.", promptTokens: 600, contextTokens: 1500).ComplexityScore, 3);
    }

    [Fact]
    public void Several_sub_questions_add_complexity()
    {
        var two = Route("What is X? What is Y?");
        var three = Route("What is X? What is Y? And Z?");
        var withKeyword = Route("Why is X slow? How do we fix it?");

        Assert.Equal(0.15, two.ComplexityScore, 3);
        Assert.Equal(0.20, three.ComplexityScore, 3);
        Assert.Equal("reasoning", withKeyword.Tier);
    }

    [Fact]
    public void Code_and_math_add_complexity()
    {
        Assert.Equal(0.20, Route("Fix this:\n```csharp\nvar x = 1;\n```").ComplexityScore, 3);
        Assert.Equal(0.20, Route("int Add(int a, int b) {\n  return a + b;\n}").ComplexityScore, 3);
        Assert.Equal(0.20, Route("Solve $x^2 + 3x = 10$ for x.").ComplexityScore, 3);
        Assert.Equal(0.20, Route("∫ x dx nedir").ComplexityScore, 3);
        Assert.Equal("reasoning", Route("Analyze this function:\n```python\ndef f(n):\n    return n * 2\n```").Tier);
    }

    [Fact]
    public void Explicit_long_output_requests_add_complexity()
    {
        Assert.Equal(0.15, Route("Write a detailed report on the incident.").ComplexityScore, 3);
        Assert.Equal(0.15, Route("Olay hakkında ayrıntılı bir rapor yaz.").ComplexityScore, 3);
        Assert.Equal(0.15, Route("Write at least 800 words on Kubernetes.").ComplexityScore, 3);
        Assert.Equal("reasoning", Route("Kubernetes ve Nomad'ı kapsamlı biçimde karşılaştır.").Tier);
    }

    [Fact]
    public void The_score_is_capped_at_one()
    {
        var route = Route(
            "Why? Compare, analyze and evaluate step by step the trade-offs, pros and cons, strategy, design and plan? " +
            "Write a detailed essay? ```code();``` $x^2$",
            promptTokens: 5_000, contextTokens: 5_000);

        Assert.Equal(1.0, route.ComplexityScore);
        Assert.Equal("reasoning", route.Tier);
    }

    [Fact]
    public void Routing_is_deterministic()
    {
        const string prompt = "Neden? Kubernetes ile Nomad'ı karşılaştır. ```yaml\na: b\n```";
        var first = Route(prompt, promptTokens: 400, contextTokens: 900);

        for (var i = 0; i < 20; i++)
        {
            var again = new ModelRouter(Monitor(new RoutingOptions()), Monitor(Catalog())).Route(new RoutingContext("t", prompt, 400, 900, null));
            Assert.Equal(first.Tier, again.Tier);
            Assert.Equal(first.Model, again.Model);
            Assert.Equal(first.ComplexityScore, again.ComplexityScore);
            Assert.Equal(first.Reasons, again.Reasons);
        }
    }

    [Fact]
    public void Reasons_never_contain_prompt_text()
    {
        const string secret = "ayse.yilmaz@contoso.com";
        var route = Route($"Why was {secret} charged twice? Compare both invoices step by step.");

        Assert.DoesNotContain(route.Reasons, r => r.Contains(secret, StringComparison.OrdinalIgnoreCase) || r.Contains("invoice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_reasoning_threshold_is_configurable()
    {
        var strict = new ModelRouter(Monitor(new RoutingOptions { ReasoningThreshold = 0.9 }), Monitor(Catalog()))
            .Route(new RoutingContext("t", "Compare A and B step by step.", 10, 0, null));
        var eager = new ModelRouter(Monitor(new RoutingOptions { ReasoningThreshold = 0.3 }), Monitor(Catalog()))
            .Route(new RoutingContext("t", "Why is the sky blue?", 10, 0, null));

        Assert.Equal("fast", strict.Tier);
        Assert.Equal("reasoning", eager.Tier);
    }

    [Theory]
    [InlineData("reasoning", "reasoning", "o4-mini")]
    [InlineData("REASONING", "reasoning", "o4-mini")]
    [InlineData("o4-mini", "reasoning", "o4-mini")]
    [InlineData("GPT-4.1-MINI", "fast", "gpt-4.1-mini")]
    [InlineData(" fast ", "fast", "gpt-4.1-mini")]
    public void An_allow_listed_requested_model_is_honoured(string requested, string tier, string model)
    {
        var route = Route("Why? Compare step by step.", requested: requested);

        Assert.Equal(tier, route.Tier);
        Assert.Equal(model, route.Model);
        Assert.Contains(route.Reasons, r => r.StartsWith("requested model honoured", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("gpt-5-pro")]
    [InlineData("o4-mini-high")]
    [InlineData("reasoning-tier")]
    public void A_requested_model_outside_the_allow_list_is_ignored(string requested)
    {
        var simple = Route("What time is it in Tokyo?", requested: requested);

        Assert.Equal("fast", simple.Tier);
        Assert.Equal("gpt-4.1-mini", simple.Model);
        Assert.Contains(simple.Reasons, r => r.Contains("ignored: not an allow-listed tier or model", StringComparison.Ordinal));
    }

    [Fact]
    public void A_requested_model_with_unusual_characters_is_not_echoed()
    {
        var route = Route("Hello", requested: "x\r\nINJECTED log line");

        Assert.Equal("fast", route.Tier);
        Assert.DoesNotContain(route.Reasons, r => r.Contains("INJECTED", StringComparison.Ordinal));
        Assert.Contains(route.Reasons, r => r.Contains("(not a plain model id)", StringComparison.Ordinal));
    }

    [Fact]
    public void Requested_models_are_ignored_when_callers_may_not_choose()
    {
        var route = new ModelRouter(Monitor(new RoutingOptions { AllowRequestedModel = false }), Monitor(Catalog()))
            .Route(new RoutingContext("t", "Hello", 1, 0, "o4-mini"));

        Assert.Equal("fast", route.Tier);
        Assert.Contains(route.Reasons, r => r.Contains("AllowRequestedModel=false", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_configured_tiers_the_built_in_defaults_are_used_and_noted()
    {
        var router = new ModelRouter(Monitor(new RoutingOptions()), Monitor(new ModelCatalogOptions()));

        var simple = router.Route(new RoutingContext("t", "Hi", 1, 0, null));
        var complex = router.Route(new RoutingContext("t", "Compare A and B step by step.", 10, 0, null));
        var requested = router.Route(new RoutingContext("t", "Hi", 1, 0, "qwen2.5:1.5b"));

        Assert.Equal(("fast", "qwen2.5:0.5b"), (simple.Tier, simple.Model));
        Assert.Equal(("reasoning", "qwen2.5:1.5b"), (complex.Tier, complex.Model));
        Assert.Equal(("reasoning", "qwen2.5:1.5b"), (requested.Tier, requested.Model));
        Assert.Contains(simple.Reasons, r => r.StartsWith("no model tiers configured", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_reasoning_tier_complex_prompts_stay_on_the_default_tier()
    {
        var catalog = new ModelCatalogOptions { DefaultTier = "standard" };
        catalog.Tiers["standard"] = new ModelTierOptions { Model = "gpt-4.1" };
        var route = new ModelRouter(Monitor(new RoutingOptions()), Monitor(catalog))
            .Route(new RoutingContext("t", "Compare A and B step by step.", 10, 0, null));

        Assert.Equal(("standard", "gpt-4.1"), (route.Tier, route.Model));
        Assert.Contains(route.Reasons, r => r.Contains("no reasoning tier is configured", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_default_tier_falls_back_deterministically()
    {
        var catalog = Catalog();
        catalog.DefaultTier = "does-not-exist";
        var route = new ModelRouter(Monitor(new RoutingOptions()), Monitor(catalog)).Route(new RoutingContext("t", "Hi", 1, 0, null));

        Assert.Equal("fast", route.Tier);
        Assert.Contains(route.Reasons, r => r.Contains("default tier 'does-not-exist' is not configured", StringComparison.Ordinal));
    }

    [Fact]
    public void Tiers_without_a_model_are_not_routable()
    {
        var catalog = Catalog();
        catalog.Tiers["reasoning"].Model = " ";
        var route = new ModelRouter(Monitor(new RoutingOptions()), Monitor(catalog))
            .Route(new RoutingContext("t", "Compare A and B step by step.", 10, 0, null));

        Assert.Equal("fast", route.Tier);
    }

    [Fact]
    public void Pathological_prompts_are_analysed_in_linear_time()
    {
        // Each would make a backtracking pattern without anchors or bounds quadratic; a timeout would show up as a reason.
        string[] prompts =
        [
            new string('a', 40_000),
            new string('1', 40_000),
            new string('$', 40_000),
            string.Concat(Enumerable.Repeat("a(", 20_000)),
            string.Concat(Enumerable.Repeat("x^", 20_000)),
            string.Concat(Enumerable.Repeat("1+", 20_000)),
            "def " + new string('a', 40_000),
            ";" + new string(' ', 40_000) + "x",
            string.Concat(Enumerable.Repeat("step ", 8_000)),
            string.Concat(Enumerable.Repeat("?", 40_000)),
        ];

        foreach (var prompt in prompts)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var route = Route(prompt, promptTokens: 10_000);

            Assert.DoesNotContain(route.Reasons, r => r.Contains("timed out", StringComparison.Ordinal));
            Assert.True(
                System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1),
                $"{prompt[..10]}... took {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        }
    }

    [Theory]
    [InlineData("KARŞILAŞTIR", "karsilastir")]
    [InlineData("İzmir'de ılık", "izmir'de ilik")]
    [InlineData("Değerlendir ÇÖZÜM", "degerlendir cozum")]
    [InlineData("i\u0307stanbul", "istanbul")]
    public void Turkish_text_is_folded_to_ascii_before_matching(string text, string folded) =>
        Assert.Equal(folded, PromptComplexity.Fold(text));

    private static ModelRoute Route(string prompt, int promptTokens = 20, int contextTokens = 0, string? requested = null) =>
        new ModelRouter(Monitor(new RoutingOptions()), Monitor(Catalog()))
            .Route(new RoutingContext("tenant", prompt, promptTokens, contextTokens, requested));

    private static ModelCatalogOptions Catalog()
    {
        var catalog = new ModelCatalogOptions();
        catalog.Tiers["fast"] = new ModelTierOptions { Model = "gpt-4.1-mini" };
        catalog.Tiers["reasoning"] = new ModelTierOptions { Model = "o4-mini" };
        return catalog;
    }

    private static TestOptionsMonitor<T> Monitor<T>(T value) => new(value);
}
