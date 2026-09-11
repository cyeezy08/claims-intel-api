using ClaimsIntel.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClaimsIntel.Api.Data;

public class ClaimsDbContext : DbContext
{
    public ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : base(options) { }

    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<Claim> Claims => Set<Claim>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Policy>(e =>
        {
            e.HasIndex(p => p.PolicyNumber).IsUnique();
            e.Property(p => p.PolicyNumber).HasMaxLength(20);
            e.Property(p => p.Nric).HasMaxLength(14);
            e.Property(p => p.AnnualPremium).HasPrecision(14, 2);
        });

        mb.Entity<Claim>(e =>
        {
            e.HasIndex(c => c.ClaimNumber).IsUnique();
            e.Property(c => c.ClaimNumber).HasMaxLength(20);
            e.Property(c => c.AmountClaimed).HasPrecision(14, 2);
            e.Property(c => c.PolicyId).IsRequired();
            e.HasOne(c => c.Policy)
             .WithMany(p => p.Claims)
             .HasForeignKey(c => c.PolicyId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
