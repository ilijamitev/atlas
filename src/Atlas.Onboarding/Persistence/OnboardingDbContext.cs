using Atlas.Onboarding.Applications;
using Atlas.Shared.AccessLog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Atlas.Onboarding.Persistence;

/// <summary>One market's onboarding database. There is one per market (GC-2026-0814 #1).</summary>
public sealed class OnboardingDbContext(DbContextOptions<OnboardingDbContext> options) : DbContext(options)
{
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Applicant> Applicants => Set<Applicant>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<StatusChange> StatusHistory => Set<StatusChange>();
    public DbSet<ProviderResult> ProviderResults => Set<ProviderResult>();
    public DbSet<AccessLogEntry> AccessLog => Set<AccessLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureAccessLog();

        modelBuilder.Entity<Application>(application =>
        {
            application.ToTable("Applications");
            application.Property(a => a.State).HasConversion<string>().HasMaxLength(30);
            application.Property(a => a.IdempotencyKey).HasMaxLength(100);
            application.HasIndex(a => a.IdempotencyKey).IsUnique();
            application.Property(a => a.RequestHash).HasMaxLength(32);
            application.Property(a => a.TokenHash).HasMaxLength(32);
            application.Property(a => a.RejectionReason).HasMaxLength(50);
            application.Property(a => a.LastError).HasMaxLength(200);
            application.Property(a => a.AccountReference).HasMaxLength(50);
            application.Property(a => a.CardReference).HasMaxLength(50);
            application.Property(a => a.BranchActivatedBy).HasMaxLength(100);
            application.Property(a => a.RowVersion).IsRowVersion();

            // The worker's claim query reads due rows in NextAttemptAt order. Only rows the worker owns have a value.
            application.HasIndex(a => a.NextAttemptAt).HasFilter("[NextAttemptAt] IS NOT NULL");

            application.HasOne(a => a.Applicant).WithOne().HasForeignKey<Applicant>(p => p.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            application.HasMany(a => a.Documents).WithOne().HasForeignKey(d => d.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            application.HasMany(a => a.History).WithOne().HasForeignKey(h => h.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            application.HasMany(a => a.ProviderResults).WithOne().HasForeignKey(r => r.ApplicationId).OnDelete(DeleteBehavior.Cascade);

            application.Ignore(a => a.IsSubmitted);
            application.Ignore(a => a.IsRestricted);
            application.Ignore(a => a.IsFinal);
            application.Ignore(a => a.IsInProgress);
            application.Ignore(a => a.CurrentIdentityDocument);
            application.Ignore(a => a.CurrentSelfie);
        });

        modelBuilder.Entity<Applicant>(applicant =>
        {
            applicant.ToTable("Applicants");
            applicant.HasKey(p => p.ApplicationId);
            applicant.Property(p => p.FirstName).HasMaxLength(100);
            applicant.Property(p => p.LastName).HasMaxLength(100);
            applicant.Property(p => p.Nationality).HasMaxLength(2).IsFixedLength();
            applicant.Property(p => p.CountryOfResidence).HasMaxLength(2).IsFixedLength();
            applicant.Property(p => p.Email).HasMaxLength(254);
            applicant.Property(p => p.Phone).HasMaxLength(16);
            applicant.Property(p => p.IdentifierType).HasMaxLength(20);
            applicant.Property(p => p.IdentifierValue).HasMaxLength(200);
            applicant.Property(p => p.IdentifierIssuingCountry).HasMaxLength(2).IsFixedLength();
        });

        modelBuilder.Entity<Document>(document =>
        {
            document.ToTable("Documents");
            document.Property(d => d.Type).HasMaxLength(20);
            document.Property(d => d.BlobName).HasMaxLength(100);
            document.Property(d => d.Sha256).HasMaxLength(32);
            document.Property(d => d.State).HasConversion<string>().HasMaxLength(10);
            document.HasIndex(d => new { d.ApplicationId, d.Type, d.Version }).IsUnique();
            document.HasIndex(d => new { d.State, d.CreatedAt });
        });

        modelBuilder.Entity<StatusChange>(change =>
        {
            change.ToTable("StatusHistory");
            change.Property(c => c.From).HasConversion<string>().HasMaxLength(30);
            change.Property(c => c.To).HasConversion<string>().HasMaxLength(30);
            change.Property(c => c.Reason).HasMaxLength(50);
        });

        modelBuilder.Entity<ProviderResult>(result =>
        {
            result.ToTable("ProviderResults");
            result.Property(r => r.Provider).HasMaxLength(20);
            result.Property(r => r.Reference).HasMaxLength(100);
            result.Property(r => r.Outcome).HasMaxLength(100);
        });
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without a running database.</summary>
internal sealed class OnboardingDesignTimeFactory : IDesignTimeDbContextFactory<OnboardingDbContext>
{
    public OnboardingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OnboardingDbContext>().UseSqlServer("Server=design-time;Database=onboarding").Options);
}
