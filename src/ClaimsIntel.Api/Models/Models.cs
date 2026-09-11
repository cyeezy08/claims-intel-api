namespace ClaimsIntel.Api.Models;

/// <summary>Insurance policy held by a policyholder.</summary>
public class Policy
{
    public int Id { get; set; }
    /// <summary>e.g. MOTOR, MEDICAL, PROPERTY, LIFE</summary>
    public required string LineOfBusiness { get; set; }
    public required string PolicyNumber { get; set; }
    public required string PolicyholderName { get; set; }
    public required string Nric { get; set; }
    public decimal AnnualPremium { get; set; }
    public DateTime InceptionDate { get; set; }
    public DateTime ExpiryDate { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<Claim> Claims { get; set; } = new List<Claim>();
}

/// <summary>Claim statuses modelled on typical Malaysian insurer SI flows.</summary>
public enum ClaimStatus
{
    Submitted = 0,
    InReview = 1,
    PendingDocuments = 2,
    Approved = 3,
    Rejected = 4,
    Settled = 5
}

public class Claim
{
    public int Id { get; set; }
    public required string ClaimNumber { get; set; }
    public int PolicyId { get; set; }
    public Policy? Policy { get; set; }
    public decimal AmountClaimed { get; set; }
    /// <summary>Free-text incident description supplied by claimant.</summary>
    public required string IncidentDescription { get; set; }
    public DateTime IncidentDate { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public ClaimStatus Status { get; set; } = ClaimStatus.Submitted;
    /// <summary>Latest fraud risk assessment (0-100) computed on submission.</summary>
    public int FraudRiskScore { get; set; }
    /// <summary>Human-readable reasons behind the risk score.</summary>
    public string RiskNotes { get; set; } = string.Empty;
    public string? AdjusterNotes { get; set; }
}

/// <summary>Request body for creating a claim.</summary>
public record CreateClaimRequest(
    int PolicyId,
    decimal AmountClaimed,
    string IncidentDescription,
    DateTime IncidentDate);

/// <summary>Request body for a status transition.</summary>
public record UpdateStatusRequest(ClaimStatus Status, string? Notes);
