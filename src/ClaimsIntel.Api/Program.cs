using System.Text.Json.Serialization;
using ClaimsIntel.Api.Data;
using ClaimsIntel.Api.Models;
using ClaimsIntel.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ClaimsDbContext>(opt =>
{
    var cs = builder.Configuration.GetConnectionString("Default");
    if (!string.IsNullOrEmpty(cs))
        opt.UseSqlServer(cs);
    else
        opt.UseInMemoryDatabase("claims-intel");
});

builder.Services.AddScoped<IFraudScoringService, RuleBasedFraudScoringService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // EF navigation properties (Policy <-> Claims) form reference cycles;
    // IgnoreCycles serializes them as null instead of throwing at depth 64.
    o.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ---------- Health ----------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithSummary("Liveness probe");

// ---------- Policies ----------
app.MapGet("/api/policies", async (ClaimsDbContext db) =>
    await db.Policies.Include(p => p.Claims).AsNoTracking().ToListAsync())
   .WithSummary("List policies with their claims");

app.MapGet("/api/policies/{id:int}", async (int id, ClaimsDbContext db) =>
    await db.Policies.Include(p => p.Claims).FirstOrDefaultAsync(p => p.Id == id) is { } policy
        ? Results.Ok(policy)
        : Results.NotFound(new { error = $"Policy {id} not found" }))
   .WithSummary("Get a policy");

app.MapPost("/api/policies", async (Policy policy, ClaimsDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(policy.PolicyNumber))
        return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["policyNumber"] = new[] { "PolicyNumber is required" } });
    policy.Id = 0;
    db.Policies.Add(policy);
    await db.SaveChangesAsync();
    return Results.Created($"/api/policies/{policy.Id}", policy);
}).WithSummary("Create a policy");

// ---------- Claims ----------
app.MapGet("/api/claims", async (
    ClaimsDbContext db,
    ClaimStatus? status = null,
    int? minRisk = null,
    int page = 1, int pageSize = 25) =>
{
    var query = db.Claims.Include(c => c.Policy).AsNoTracking();
    if (status is not null)
    {
        var s = status.Value;
        query = query.Where(c => c.Status == s);
    }
    if (minRisk is not null)
    {
        var r = minRisk.Value;
        query = query.Where(c => c.FraudRiskScore >= r);
    }
    var total = await query.CountAsync();
    var items = await query
        .OrderByDescending(c => c.FraudRiskScore)
        .ThenByDescending(c => c.SubmittedAt)
        .Skip((page - 1) * pageSize)
        .Take(Math.Min(pageSize, 100))
        .ToListAsync();
    return Results.Ok(new { total, page, pageSize, data = items });
}).WithSummary("List claims — filter by status / minimum fraud risk");

app.MapGet("/api/claims/{id:int}", async (int id, ClaimsDbContext db) =>
    await db.Claims.Include(c => c.Policy).FirstOrDefaultAsync(c => c.Id == id) is { } claim
        ? Results.Ok(claim)
        : Results.NotFound(new { error = $"Claim {id} not found" }))
   .WithSummary("Get a claim");

app.MapPost("/api/claims", async (CreateClaimRequest req, ClaimsDbContext db, IFraudScoringService scorer) =>
{
    var policy = await db.Policies.Include(p => p.Claims)
                                  .FirstOrDefaultAsync(p => p.Id == req.PolicyId);
    if (policy is null) return Results.NotFound(new { error = $"Policy {req.PolicyId} not found" });
    if (!policy.IsActive) return Results.UnprocessableEntity(new { error = "Policy is inactive" });
    if (req.AmountClaimed <= 0) return Results.ValidationProblem(new Dictionary<string, string[]>
        { ["amountClaimed"] = new[] { "Amount must be positive" } });
    if (req.IncidentDate < policy.InceptionDate || req.IncidentDate > DateTime.UtcNow.AddDays(1))
        return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["incidentDate"] = new[] { "Incident date outside policy period" } });

    var claim = new Claim
    {
        ClaimNumber = GenerateClaimNumber(),
        PolicyId = policy.Id,
        AmountClaimed = req.AmountClaimed,
        IncidentDescription = req.IncidentDescription,
        IncidentDate = req.IncidentDate,
    };

    var assessment = scorer.Score(claim, policy);
    claim.FraudRiskScore = assessment.Score;
    claim.RiskNotes = string.Join("; ", assessment.Reasons);
    // Auto-route high-risk claims straight to SI review
    claim.Status = assessment.Score >= 60 ? ClaimStatus.InReview : ClaimStatus.Submitted;

    db.Claims.Add(claim);
    await db.SaveChangesAsync();
    return Results.Created($"/api/claims/{claim.Id}", claim);
}).WithSummary("Submit a claim — runs fraud risk scoring on intake");

app.MapMethods("/api/claims/{id:int}/status", new[] { "PATCH" }, async (int id, UpdateStatusRequest req, ClaimsDbContext db) =>
{
    var claim = await db.Claims.FindAsync(id);
    if (claim is null) return Results.NotFound(new { error = $"Claim {id} not found" });

    // Allowed transition matrix prevents e.g. Settled -> Submitted
    var allowed = new Dictionary<ClaimStatus, ClaimStatus[]>
    {
        [ClaimStatus.Submitted] = [ClaimStatus.InReview, ClaimStatus.PendingDocuments, ClaimStatus.Approved, ClaimStatus.Rejected],
        [ClaimStatus.InReview] = [ClaimStatus.PendingDocuments, ClaimStatus.Approved, ClaimStatus.Rejected],
        [ClaimStatus.PendingDocuments] = [ClaimStatus.InReview, ClaimStatus.Approved, ClaimStatus.Rejected],
        [ClaimStatus.Approved] = [ClaimStatus.Settled],
        [ClaimStatus.Rejected] = [ClaimStatus.InReview],
        [ClaimStatus.Settled] = [],
    };
    if (!allowed[claim.Status].Contains(req.Status))
        return Results.UnprocessableEntity(new
        {
            error = $"Illegal transition {claim.Status} -> {req.Status}",
            allowedFrom = allowed[claim.Status].Select(s => s.ToString())
        });

    claim.Status = req.Status;
    claim.AdjusterNotes = req.Notes ?? claim.AdjusterNotes;
    await db.SaveChangesAsync();
    return Results.Ok(claim);
}).WithSummary("Transition claim status — enforced state machine");

app.MapGet("/api/claims/risk/summary", async (ClaimsDbContext db) =>
{
    var buckets = await db.Claims
        .GroupBy(c => c.FraudRiskScore / 25)
        .Select(g => new { Bucket = g.Key, Count = g.Count() })
        .ToListAsync();
    return Results.Ok(buckets.ToDictionary(b => $"risk_{b.Bucket * 25}-{b.Bucket * 25 + 24}", b => b.Count));
}).WithSummary("Fraud risk distribution histogram");

static string GenerateClaimNumber() => $"CL-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}";

app.Run();

// Exposes Program for integration tests
public partial class Program { }
