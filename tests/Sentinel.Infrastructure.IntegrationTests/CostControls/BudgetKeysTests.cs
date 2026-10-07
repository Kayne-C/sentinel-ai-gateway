using Sentinel.Infrastructure.CostControls.Budgets;

namespace Sentinel.Infrastructure.IntegrationTests.CostControls;

public sealed class BudgetKeysTests
{
    private static readonly DateTimeOffset Now = new(2099, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Keys_follow_the_documented_layout()
    {
        Assert.Equal("budget:{contoso.com}:m:209903", BudgetKeys.Tenant("contoso.com", BudgetPeriods.Month(Now)));
        Assert.Equal("budget:{contoso.com}:user%3A1:d:20990310", BudgetKeys.Subject("contoso.com", "user:1", BudgetPeriods.Day(Now)));
    }

    [Theory]
    [InlineData("4f9b7c5e-2d1a-4b8e-9c3f-0a1b2c3d4e5f", "4f9b7c5e-2d1a-4b8e-9c3f-0a1b2c3d4e5f")]
    [InlineData("user@contoso.com", "user@contoso.com")]
    [InlineData("a:b", "a%3Ab")]
    [InlineData("{tag}", "%7Btag%7D")]
    [InlineData("50%", "50%25")]
    [InlineData("Contoso Ltd", "Contoso%20Ltd")]
    [InlineData("İş", "%C4%B0%C5%9F")]
    [InlineData("😀", "%F0%9F%98%80")]
    public void Ids_are_percent_encoded(string id, string encoded) => Assert.Equal(encoded, BudgetKeys.Encode(id));

    [Fact]
    public void Distinct_tenant_and_subject_pairs_never_share_a_key()
    {
        var day = BudgetPeriods.Day(Now);
        (string Tenant, string Subject)[] pairs =
        [
            ("a:b", "c"), ("a", "b:c"), ("a", "b%3Ac"), ("a%3Ab", "c"), ("a}:{b", "c"), ("a", "}:{b:c"), ("A:b", "c"), ("a:b", "C"),
        ];

        var keys = pairs.Select(p => BudgetKeys.Subject(p.Tenant, p.Subject, day)).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.Equal(1, k.Count(c => c == '{')));
        Assert.All(keys, k => Assert.Equal(1, k.Count(c => c == '}')));
    }

    [Fact]
    public void Ids_that_cannot_be_encoded_injectively_are_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => BudgetKeys.Encode(""));
        Assert.ThrowsAny<ArgumentException>(() => BudgetKeys.Encode("   "));
        Assert.ThrowsAny<ArgumentException>(() => BudgetKeys.Encode("lone" + (char)0xD800));
        Assert.ThrowsAny<ArgumentException>(() => BudgetKeys.Encode((char)0xDC00 + "lone"));
        Assert.ThrowsAny<ArgumentException>(() => BudgetKeys.Encode(new string('x', BudgetKeys.MaxIdLength + 1)));
    }

    [Theory]
    [InlineData("2099-03-10T12:00:00Z", "209903", "2099-04-01T00:00:00Z", "20990310", "2099-03-11T00:00:00Z")]
    [InlineData("2099-12-31T23:59:59Z", "209912", "2100-01-01T00:00:00Z", "20991231", "2100-01-01T00:00:00Z")]
    [InlineData("2100-02-28T08:00:00Z", "210002", "2100-03-01T00:00:00Z", "21000228", "2100-03-01T00:00:00Z")]
    [InlineData("2099-03-10T23:30:00-05:00", "209903", "2099-04-01T00:00:00Z", "20990311", "2099-03-12T00:00:00Z")]
    public void Periods_are_utc_calendar_months_and_days(string now, string monthId, string monthEnd, string dayId, string dayEnd)
    {
        var instant = DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture);

        var month = BudgetPeriods.Month(instant);
        var day = BudgetPeriods.Day(instant);

        Assert.Equal(monthId, month.Id);
        Assert.Equal(DateTimeOffset.Parse(monthEnd, System.Globalization.CultureInfo.InvariantCulture), month.EndsAt);
        Assert.Equal(dayId, day.Id);
        Assert.Equal(DateTimeOffset.Parse(dayEnd, System.Globalization.CultureInfo.InvariantCulture), day.EndsAt);
    }

    [Fact]
    public void Counters_outlive_their_period_by_the_grace_and_never_get_a_non_positive_ttl()
    {
        var day = BudgetPeriods.Day(Now);

        Assert.Equal(TimeSpan.FromHours(13), BudgetPeriods.TimeToLive(day, Now));
        Assert.Equal(TimeSpan.FromSeconds(1), BudgetPeriods.TimeToLive(day, Now.AddDays(3)));
    }
}
