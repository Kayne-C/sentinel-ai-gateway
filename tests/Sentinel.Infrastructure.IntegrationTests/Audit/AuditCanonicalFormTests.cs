using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Sentinel.Domain.Audit;
using Sentinel.Infrastructure.Audit;

namespace Sentinel.Infrastructure.IntegrationTests.Audit;

public sealed class AuditCanonicalFormTests
{
    private const string PreviousHash = "5f1d2c3b4a5968778695a4b3c2d1e0f00112233445566778899aabbccddeeff0";

    /// <summary>SHA-256 of <see cref="GoldenInput"/>, computed independently (Python <c>hashlib</c>), not by this code base.</summary>
    private const string PinnedGoldenHash = "b966126831303e2c12b1882ad421871b417be5d9d490cb4f914841a80ba45bf2";

    /// <summary>
    /// The hash input spelled out by hand: if this test passes on a machine, that machine hashes entries exactly like
    /// every other one. Values are chosen to expose culture-sensitive formatting (tr-TR/de-DE decimal comma), newline
    /// and quote injection, and dotted/dotless I.
    /// </summary>
    private static readonly string[] GoldenInput =
    [
        PreviousHash,
        "sentinel.audit/v1",
        "\"contoso\"",
        "7",
        "2026-03-14T09:26:53.1234567Z",
        "\"user-1\"",
        "\"Ask\"",
        "\"Blocked\"",
        "\"qwen2.5:1.5b\"",
        "\"fast\"",
        "1200",
        "-3",
        "1234.5",
        "\"{\\\"Email\\\":2,\\\"Iban\\\":1}\"",
        "\"[\\\"injection.override\\\"]\"",
        "\"[]\"",
        "\"ab12\"",
        "\"Line one\\u000a\\\"quoted\\\" \\\\ [EMAIL_1]\"",
        "null",
        "1234.5",
        "null",
        "\"\u0130stanbul/\u0131i\"",
    ];

    [Fact]
    public void The_hash_covers_exactly_the_documented_canonical_text()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', GoldenInput))));

        Assert.Equal(expected, AuditCanonicalForm.ComputeHash(GoldenRecord()));
    }

    [Fact]
    public void A_known_entry_keeps_its_pinned_hash_under_the_turkish_culture()
    {
        using var turkish = CultureScope.Use("tr-TR");

        Assert.Equal(PinnedGoldenHash, AuditCanonicalForm.ComputeHash(GoldenRecord()));
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("fa-IR")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public void Normalising_and_hashing_an_event_does_not_depend_on_the_current_culture(string culture)
    {
        string invariant;
        using (CultureScope.Use(string.Empty))
        {
            invariant = HashOf(CultureSensitiveEvent());
        }

        using (CultureScope.Use(culture))
        {
            Assert.Equal(invariant, HashOf(CultureSensitiveEvent()));
        }
    }

    [Fact]
    public void Pii_counts_are_stored_with_ordinally_sorted_keys_under_the_turkish_culture()
    {
        using var turkish = CultureScope.Use("tr-TR");

        var record = AuditEntryMapper.ToTemplate(CultureSensitiveEvent(), storeRedactedPrompt: true);

        Assert.Equal("{\"IBAN\":2,\"Iban\":3,\"a\":5,\"ip\":1,\"\\u0130\":4}", record.RedactedPiiJson);
    }

    [Fact]
    public void Changing_any_persisted_column_changes_the_hash()
    {
        var original = GoldenRecord();
        var originalHash = AuditCanonicalForm.ComputeHash(original);
        var columns = typeof(AuditEntryRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name is not (nameof(AuditEntryRecord.Id) or nameof(AuditEntryRecord.Hash)))
            .ToList();

        // Reflection, so a column added later without being added to the canonical form fails here.
        Assert.Equal(21, columns.Count);
        foreach (var column in columns)
        {
            var changed = original.ChainAt(original.Sequence, original.PreviousHash);
            column.SetValue(changed, Mutate(column.GetValue(changed), column.PropertyType));

            Assert.True(
                AuditCanonicalForm.ComputeHash(changed) != originalHash,
                $"Changing {column.Name} did not change the hash.");
        }
    }

    [Fact]
    public void Null_and_empty_text_hash_differently()
    {
        var withNull = GoldenRecord();
        withNull.Model = null;
        var withEmpty = GoldenRecord();
        withEmpty.Model = string.Empty;

        Assert.NotEqual(AuditCanonicalForm.ComputeHash(withNull), AuditCanonicalForm.ComputeHash(withEmpty));
    }

    [Fact]
    public void Moving_text_across_a_field_boundary_changes_the_hash()
    {
        var original = GoldenRecord();
        original.Model = "gpt\nfast";
        original.ModelTier = "x";
        var shifted = GoldenRecord();
        shifted.Model = "gpt";
        shifted.ModelTier = "fast\nx";

        Assert.NotEqual(AuditCanonicalForm.ComputeHash(original), AuditCanonicalForm.ComputeHash(shifted));
    }

    [Fact]
    public void Unpaired_surrogates_that_utf8_would_merge_still_hash_differently()
    {
        var hashes = new[] { "\uD800", "\uD801", "\uFFFD", "\uDC00" }
            .Select(value =>
            {
                var record = GoldenRecord();
                record.Subject = value;
                return AuditCanonicalForm.ComputeHash(record);
            });

        Assert.Equal(4, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_cost_read_back_with_the_column_scale_hashes_like_the_value_that_was_written()
    {
        var written = GoldenRecord();
        written.EstimatedCostUsd = 0.0024m;
        var readBack = GoldenRecord();
        readBack.EstimatedCostUsd = 0.00240000m;

        Assert.Equal(AuditCanonicalForm.ComputeHash(written), AuditCanonicalForm.ComputeHash(readBack));
    }

    [Fact]
    public void A_timestamp_read_back_without_its_kind_hashes_like_the_utc_value_that_was_written()
    {
        var written = GoldenRecord();
        var readBack = GoldenRecord();
        readBack.OccurredAtUtc = DateTime.SpecifyKind(written.OccurredAtUtc, DateTimeKind.Unspecified);

        Assert.Equal(AuditCanonicalForm.ComputeHash(written), AuditCanonicalForm.ComputeHash(readBack));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0.00000000", "0")]
    [InlineData("1.50000000", "1.5")]
    [InlineData("0.00000001", "0.00000001")]
    [InlineData("-0.00000001", "-0.00000001")]
    [InlineData("9999999999.99999999", "9999999999.99999999")]
    [InlineData("0.123456789", "0.123456789")]
    public void Money_renders_one_scale_free_invariant_form_per_value(string value, string expected)
    {
        using var turkish = CultureScope.Use("tr-TR");

        Assert.Equal(expected, AuditCanonicalForm.Money(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Negative_zero_latency_renders_like_zero_because_sql_server_stores_it_as_zero()
    {
        Assert.Equal(AuditCanonicalForm.Real(0d), AuditCanonicalForm.Real(-0d));
        Assert.Equal("12.25", AuditCanonicalForm.Real(12.25));
    }

    private static string HashOf(AuditEvent auditEvent) => AuditEntryMapper.ToTemplate(auditEvent, storeRedactedPrompt: true).ChainAt(1, null).Hash;

    private static AuditEvent CultureSensitiveEvent() => new()
    {
        TenantId = "contoso",
        SubjectId = "user-1",
        Operation = AuditOperation.ChatCompletion,
        Outcome = AuditOutcome.Allowed,
        OccurredAtUtc = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999),
        Model = "gpt-4.1-mini",
        ModelTier = "reasoning",
        PromptTokens = 1_234_567,
        CompletionTokens = -1,
        EstimatedCostUsd = -1234.56789m,
        RedactedPii = new Dictionary<string, int> { ["ip"] = 1, ["IBAN"] = 2, ["Iban"] = 3, ["\u0130"] = 4, ["a"] = 5 },
        GuardrailFindings = ["injection.override", "pii.output"],
        Sources = ["\u0130zmir-\u0131:2"],
        LatencyMs = 1234.0625,
    };

    private static AuditEntryRecord GoldenRecord() => new()
    {
        TenantId = "contoso",
        Sequence = 7,
        OccurredAtUtc = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc).AddTicks(1234567),
        SubjectId = "user-1",
        Operation = "Ask",
        Outcome = "Blocked",
        Model = "qwen2.5:1.5b",
        ModelTier = "fast",
        PromptTokens = 1200,
        CompletionTokens = -3,
        EstimatedCostUsd = 1234.5m,
        RedactedPiiJson = "{\"Email\":2,\"Iban\":1}",
        GuardrailFindingsJson = "[\"injection.override\"]",
        SourcesJson = "[]",
        PromptDigest = "ab12",
        RedactedPrompt = "Line one\n\"quoted\" \\ [EMAIL_1]",
        ErrorCode = null,
        LatencyMs = 1234.5,
        TraceId = null,
        Subject = "\u0130stanbul/\u0131i",
        PreviousHash = PreviousHash,
    };

    private static object? Mutate(object? value, Type type) => value switch
    {
        string text => text + "x",
        null when type == typeof(string) => "x",
        long number => number + 1,
        int number => number + 1,
        decimal money => money + 0.00000001m,
        double real => real + 0.25,
        DateTime timestamp => timestamp.AddTicks(1),
        _ => throw new InvalidOperationException($"No mutation for {type.Name}; extend the test with the new column type."),
    };
}
