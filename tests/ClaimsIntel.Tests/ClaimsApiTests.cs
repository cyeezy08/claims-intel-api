using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ClaimsIntel.Api.Models;
using ClaimsIntel.Api.Data;
using Xunit;

namespace ClaimsIntel.Tests;

/// <summary>End-to-end API tests using the in-memory database.</summary>
public class ClaimsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ClaimsApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddDbContext<ClaimsDbContext>(opt =>
                    opt.UseInMemoryDatabase("test-db"));
            });
        });
    }

    [Fact]
    public async Task SubmitClaim_HighRisk_AutoRoutesToReview()
    {
        var client = _factory.CreateClient();

        var policy = new Policy
        {
            LineOfBusiness = "MOTOR",
            PolicyNumber = $"POL-{Guid.NewGuid():N}",
            PolicyholderName = "High Risk Ray",
            Nric = "880202-14-5000",
            AnnualPremium = 500m,
            InceptionDate = DateTime.UtcNow.AddDays(-10),
            ExpiryDate = DateTime.UtcNow.AddYears(1),
        };
        var policyResp = await client.PostAsJsonAsync("/api/policies", policy);
        policyResp.EnsureSuccessStatusCode();
        var created = await policyResp.Content.ReadFromJsonAsync<Policy>();
        Assert.NotNull(created);

        var claimResp = await client.PostAsJsonAsync("/api/claims", new
        {
            policyId = created!.Id,
            amountClaimed = 100_000m,
            incidentDescription = "total loss - vehicle stolen, cash inside",
            incidentDate = DateTime.UtcNow,
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, claimResp.StatusCode);

        var claim = await claimResp.Content.ReadFromJsonAsync<Claim>();
        Assert.NotNull(claim);
        Assert.True(claim!.FraudRiskScore >= 60, $"risk {claim.FraudRiskScore}");
        Assert.Equal(ClaimStatus.InReview, claim.Status);
    }

    [Fact]
    public async Task IllegalStatusTransition_IsRejected()
    {
        var client = _factory.CreateClient();

        var policy = new Policy
        {
            LineOfBusiness = "MEDICAL",
            PolicyNumber = $"POL-{Guid.NewGuid():N}",
            PolicyholderName = "Calm Clara",
            Nric = "950505-14-5001",
            AnnualPremium = 2_400m,
            InceptionDate = DateTime.UtcNow.AddYears(-1),
            ExpiryDate = DateTime.UtcNow.AddYears(1),
        };
        var policyResp = await client.PostAsJsonAsync("/api/policies", policy);
        var created = await policyResp.Content.ReadFromJsonAsync<Policy>();

        var claimResp = await client.PostAsJsonAsync("/api/claims", new
        {
            policyId = created!.Id,
            amountClaimed = 150m,
            incidentDescription = "clinic visit, receipts attached",
            incidentDate = DateTime.UtcNow.AddDays(-1),
        });
        var claim = await claimResp.Content.ReadFromJsonAsync<Claim>();

        // Submitted -> Settled is illegal (must be approved first)
        var bad = await client.PatchAsJsonAsync($"/api/claims/{claim!.Id}/status",
            new { status = "Settled" });
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        // Submitted -> Approved -> Settled is legal
        var ok1 = await client.PatchAsJsonAsync($"/api/claims/{claim.Id}/status",
            new { status = "Approved", notes = "straightforward" });
        ok1.EnsureSuccessStatusCode();
        var ok2 = await client.PatchAsJsonAsync($"/api/claims/{claim.Id}/status",
            new { status = "Settled" });
        ok2.EnsureSuccessStatusCode();
    }
}
