# ClaimsIntel API

Insurance claims management API with **transparent fraud risk scoring** — built with **C# / .NET 8 / EF Core** (ASP.NET Core minimal APIs), MSSQL-ready with EF Core provider config.

![CI](https://github.com/cyeezy08/claims-intel-api/actions/workflows/ci.yml/badge.svg)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet)
![Tests](https://img.shields.io/badge/tests-xUnit-success)
![License](https://img.shields.io/badge/license-MIT-green)

## Domain background

In Malaysian insurance operations every motor/medical claim passes through a triage step: cheap obvious claims should auto-approve fast (customer experience), while suspicious ones must route to Special Investigations *before* any payment commitment. ClaimsIntel implements exactly that intake pipeline:

1. **Intake validation** — policy exists, active, incident date within policy period, positive amount
2. **Fraud risk scoring** — explainable rule engine (0–100) with reasons attached
3. **Auto-routing** — score ≥ 60 lands directly in `InReview`, otherwise `Submitted`
4. **State machine** — status transitions are constrained (you cannot jump `Submitted → Settled`), so the audit trail stays trustworthy

## Fraud scoring — the rules

Every rule adds weighted points and records a human-readable reason (`RiskNotes` on the claim):

| Signal | Weight | Rationale |
|---|---|---|
| Amount ≥ RM 50,000 | +25 | High exposure needs human eyes |
| Claim > 20× annual premium | +20 | Moral hazard: claiming far beyond what was paid in |
| Incident ≤ 30 days after inception | +25 | Classic "insure-then-lose" pattern |
| ≥ 2 prior claims in last 90 days | +15 each (max 3) | Loss-ratio abuse |
| ≥ 2 suspicious keywords in description | +10 each | "cash", "stolen", "no witness", "total loss", "fire", "lost receipt" |
| Round-figure amount (≥ 4 trailing zeros) | +8 | Fabricated amounts skew round |

The engine is **deliberately explainable over clever** — SI adjusters must justify referral decisions to regulators and customers. A statistical/ML model can sit on top later; rules stay as the auditable baseline and cold-start fallback. Score cap: 100.

## Claim lifecycle (enforced state machine)

```
Submitted ──► InReview ──► Approved ──► Settled
    │  ▲          │            │
    ▼  │          ▼            │
PendingDocuments   Rejected ───┘  (Rejected can reopen to InReview)
```

Illegal transitions return `422` with the list of allowed next states — the API will never let a claim skip approval or resurrect after settlement.

## Quick start

```bash
# In-memory DB (zero setup) + Swagger
dotnet run --project src/ClaimsIntel.Api
# → http://localhost:5100/swagger

# Or point at SQL Server
# appsettings: ConnectionStrings:Default="Server=...;Database=ClaimsIntel;..."
```

### Try it

```bash
# 1. Create a policy
curl -X POST localhost:5100/api/policies -H 'Content-Type: application/json' -d '{
  "lineOfBusiness": "MOTOR", "policyNumber": "POL-2026-0001",
  "policyholderName": "Ali bin Abu", "nric": "910101-14-5555",
  "annualPremium": 800, "inceptionDate": "2026-09-01T00:00:00Z",
  "expiryDate": "2027-09-01T00:00:00Z" }'

# 2. Submit a suspicious claim 10 days in
curl -X POST localhost:5100/api/claims -H 'Content-Type: application/json' -d '{
  "policyId": 1, "amountClaimed": 100000,
  "incidentDescription": "total loss - stolen, cash, no witness",
  "incidentDate": "2026-09-11T00:00:00Z" }'
# → 201 with fraudRiskScore 85+, status "InReview", riskNotes populated

# 3. Review the SI queue
curl 'localhost:5100/api/claims?minRisk=60'
```

## API surface

| Method | Endpoint | Purpose |
|---|---|---|
| GET | `/health` | Liveness |
| GET/POST | `/api/policies` | List / create policies |
| GET | `/api/claims?status=&minRisk=&page=` | Filterable claim queue (risk-desc) |
| POST | `/api/claims` | Submit claim → scored + routed on intake |
| PATCH | `/api/claims/:id/status` | Enforced status transition |
| GET | `/api/claims/risk/summary` | Risk histogram for dashboards |

## Testing

- **Unit tests** cover the scoring engine edge cases (zero-score baseline, weight accumulation, history signals, 100-cap)
- **Integration tests** spin up the real API with in-memory EF Core and verify auto-routing and the transition matrix end-to-end

```bash
dotnet test
```

## Roadmap

- [ ] JWT auth + adjuster roles (admin / adjuster / read-only)
- [ ] Document upload endpoints (photos, police reports) with virus scan
- [ ] ML scoring service (gradient boosting on anonymised claims) behind the same interface
- [ ] Outbox pattern → notify adjusters via email/WhatsApp on SI referral

## License

MIT
