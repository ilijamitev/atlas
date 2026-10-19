using Atlas.Screening.Reviews;
using Atlas.Screening.Screenings;
using Atlas.Shared.AccessLog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Atlas.Screening.Persistence;

/// <summary>One market's screening database. There is one per market (GC-2026-0814 #1).</summary>
public sealed class ScreeningDbContext(DbContextOptions<ScreeningDbContext> options) : DbContext(options)
{
    public DbSet<ScreeningRecord> Screenings => Set<ScreeningRecord>();
    public DbSet<ScreeningMatch> Matches => Set<ScreeningMatch>();
    public DbSet<ReviewCase> ReviewCases => Set<ReviewCase>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureAccessLog();

        modelBuilder.Entity<ScreeningRecord>(screening =>
        {
            screening.ToTable("Screenings");
            screening.HasKey(s => s.ApplicationId);
            screening.Property(s => s.ApplicationId).ValueGeneratedNever();
            screening.Property(s => s.FirstName).HasMaxLength(100);
            screening.Property(s => s.LastName).HasMaxLength(100);
            screening.Property(s => s.Nationality).HasMaxLength(2).IsFixedLength();
            screening.Property(s => s.Status).HasMaxLength(10);
            screening.Property(s => s.WorldCheckCaseId).HasMaxLength(50);
            screening.Property(s => s.RowVersion).IsRowVersion();
            screening.HasMany(s => s.Matches).WithOne().HasForeignKey(m => m.ApplicationId);
            screening.HasOne(s => s.Case).WithOne(c => c.Screening).HasForeignKey<ReviewCase>(c => c.ApplicationId);
        });

        modelBuilder.Entity<ScreeningMatch>(match =>
        {
            match.ToTable("ScreeningMatches");
            match.Property(m => m.ListType).HasMaxLength(20);
            match.Property(m => m.Subject).HasMaxLength(200);
        });

        modelBuilder.Entity<ReviewCase>(reviewCase =>
        {
            reviewCase.ToTable("ReviewCases");
            reviewCase.HasIndex(c => c.ApplicationId).IsUnique();
            reviewCase.HasIndex(c => new { c.Decision, c.DueAt });
            reviewCase.Property(c => c.Decision).HasMaxLength(10);
            reviewCase.Property(c => c.Reason).HasMaxLength(1000);
            reviewCase.Property(c => c.DecidedBy).HasMaxLength(100);
            reviewCase.Property(c => c.RowVersion).IsRowVersion();
            reviewCase.Ignore(c => c.IsOpen);
        });
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without a running database.</summary>
internal sealed class ScreeningDesignTimeFactory : IDesignTimeDbContextFactory<ScreeningDbContext>
{
    public ScreeningDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ScreeningDbContext>().UseSqlServer("Server=design-time;Database=screening").Options);
}
