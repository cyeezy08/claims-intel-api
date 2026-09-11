using ClaimsIntel.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaimsIntel.Api.Services;

/// <summary>
/// Rule-based fraud risk scoring for new claims (0 = low, 100 = high).
///
/// Deliberately transparent and explainable — every score comes with the
/// reasons that triggered it, because SI (Special Investigations) adjusters
/// must be able to justify referral decisions. A statistical model can be
/// layered on top later; the rule engine stays as the fallback & baseline.
/// </summary>
public interface IFraudScoringService
{
    FraudAssessment Score(Claim claim, Policy policy);
}

public record FraudAssessment(int Score, IReadOnlyList<string> Reasons);

public class RuleBasedFraudScoringService : IFraudScoringService
{
    // Tunable thresholds - would live in config/DB in production
    private const decimal HighAmountThreshold = 50_000m;
    private const decimal PremiumMultipleThreshold = 20m;
    private const int EarlyClaimDays = 30;
    private const int RecentClaimDays = 90;
    private const int RoundAmountThreshold = 4; // trailing zeros count

    public FraudAssessment Score(Claim claim, Policy policy)
    {
        var reasons = new List<string>();
        int score = 0;

        // 1. Amount severity
        if (claim.AmountClaimed >= HighAmountThreshold)
        {
            score += 25;
            reasons.Add($"High claim amount (RM {claim.AmountClaimed:N0} ≥ RM {HighAmountThreshold:N0})");
        }

        // 2. Claim value vs premium paid (moral hazard signal)
        if (policy.AnnualPremium > 0 &&
            claim.AmountClaimed > policy.AnnualPremium * PremiumMultipleThreshold)
        {
            score += 20;
            reasons.Add($"Claim exceeds {PremiumMultipleThreshold}x annual premium");
        }

        // 3. Early claim after inception
        var daysSinceInception = (claim.IncidentDate - policy.InceptionDate).TotalDays;
        if (daysSinceInception <= EarlyClaimDays && daysSinceInception >= -7)
        {
            score += 25;
            reasons.Add($"Incident within {EarlyClaimDays} days of policy inception");
        }

        // 4. Loss ratio history: number of claims in the last year
        //    (requires knowledge of prior claims; injected via claim.Policy.Claims)
        var cutoff = claim.SubmittedAt.AddDays(-RecentClaimDays);
        var priorClaims = policy.Claims
            .Where(c => c.Id != claim.Id && c.SubmittedAt >= cutoff)
            .ToList();
        if (priorClaims.Count >= 2)
        {
            score += 15 * Math.Min(priorClaims.Count, 3);
            reasons.Add($"{priorClaims.Count} claims in the last {RecentClaimDays} days");
        }

        // 5. Suspicious description heuristics
        var desc = claim.IncidentDescription?.ToLowerInvariant() ?? string.Empty;
        var keywords = new[] { "cash", "no witness", "stolen", "lost receipt", "total loss", "fire" };
        var hits = keywords.Where(desc.Contains).ToList();
        if (hits.Count >= 2)
        {
            score += 10 * hits.Count;
            reasons.Add($"Suspicious keywords in description: {string.Join(", ", hits)}");
        }

        // 6. Round-number amounts (classic fabrication signal)
        int trailingZeros = CountTrailingZeros(claim.AmountClaimed);
        if (trailingZeros >= RoundAmountThreshold)
        {
            score += 8;
            reasons.Add("Round-figure claim amount");
        }

        return new FraudAssessment(Math.Min(score, 100), reasons);
    }

    private static int CountTrailingZeros(decimal amount)
    {
        int zeros = 0;
        var value = amount;
        while (value % 10 == 0 && value >= 10)
        {
            value /= 10;
            zeros++;
        }
        return zeros;
    }
}
