using ClaimsIntel.Api.Models;
using ClaimsIntel.Api.Services;
using Xunit;

namespace ClaimsIntel.Tests;

public class FraudScoringServiceTests
{
    private readonly RuleBasedFraudScoringService _svc = new();

    private static Policy MakePolicy(decimal premium = 3_000m, DateTime? inception = null) =>
        new()
        {
            Id = 1,
            LineOfBusiness = "MOTOR",
            PolicyNumber = "POL-001",
            PolicyholderName = "Test User",
            Nric = "900101-14-5555",
            AnnualPremium = premium,
            InceptionDate = inception ?? new DateTime(2026, 1, 1),
            ExpiryDate = new DateTime(2026, 12, 31),
        };

    private static Claim MakeClaim(decimal amount, string desc, DateTime incident) =>
        new()
        {
            Id = 0,
            ClaimNumber = "CL-X",
            PolicyId = 1,
            AmountClaimed = amount,
            IncidentDescription = desc,
            IncidentDate = incident,
        };

    [Fact]
    public void LowValueMatureClaim_ScoresZero()
    {
        var policy = MakePolicy(inception: new DateTime(2025, 1, 1));
        var claim = MakeClaim(1_234m, "Rear bumper dented in car park, dashcam footage available",
            new DateTime(2025, 8, 15));
        var result = _svc.Score(claim, policy);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void HighRoundAmount_EarlyClaim_AccumulatesScore()
    {
        var inception = new DateTime(2026, 3, 1);
        var policy = MakePolicy(premium: 1_000m, inception: inception);
        // RM 100,000 round figure, 15 days after inception, > 20x premium
        var claim = MakeClaim(100_000m, "Total loss - vehicle gone", inception.AddDays(15));
        var result = _svc.Score(claim, policy);
        // amount(25) + premium multiple(20) + early claim(25) + round figure(8) + keywords
        Assert.True(result.Score >= 78, $"expected >= 78, got {result.Score}");
        Assert.Contains(result.Reasons, r => r.Contains("inception"));
        Assert.Contains(result.Reasons, r => r.Contains("premium"));
        Assert.Contains(result.Reasons, r => contains("Round-figure", r));
    }

    [Fact]
    public void RepeatedClaims_History_IncreasesScore()
    {
        var policy = MakePolicy();
        var recent = DateTime.UtcNow.AddDays(-10);
        var c1 = MakeClaim(2_000m, "Windscreen", DateTime.UtcNow.AddDays(-20)); c1.Id = 10;
        var c2 = MakeClaim(3_000m, "Side mirror", DateTime.UtcNow.AddDays(-15)); c2.Id = 11;
        var claim = MakeClaim(4_000m, "Another scratch", recent); claim.Id = 12;
        policy.Claims.Add(c1);
        policy.Claims.Add(c2);
        policy.Claims.Add(claim);

        var result = _svc.Score(claim, policy);
        Assert.True(result.Score >= 30, $"expected >= 30 from history, got {result.Score}");
        Assert.Contains(result.Reasons, r => r.Contains("claims in the last"));
    }

    [Fact]
    public void Score_IsCappedAt100()
    {
        var inception = DateTime.UtcNow.AddDays(-5);
        var policy = MakePolicy(premium: 100m, inception: inception);
        var claim = MakeClaim(10_000_000m, "total loss cash stolen fire no witness lost receipt",
            DateTime.UtcNow);
        for (int i = 0; i < 5; i++)
            policy.Claims.Add(MakeClaim(9_999m, "x", DateTime.UtcNow.AddDays(-2)));
        policy.Claims.Add(claim);

        var result = _svc.Score(claim, policy);
        Assert.True(result.Score <= 100);
        Assert.Equal(100, result.Score);
    }

    private static bool contains(string needle, string haystack) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
