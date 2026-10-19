using System.Diagnostics;
using System.Security.Claims;
using Atlas.Shared.Auth;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Shared.AccessLog;

/// <summary>
/// One access to a customer record (GC-2026-0814 #2): who, what, when. It holds IDs only, never personal data, and
/// lives in the market's own database, kept as long as the record it covers.
/// </summary>
public sealed class AccessLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string ActorType { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Resource { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string? TraceId { get; set; }
}

public enum AccessAction
{
    Create,
    Read,
    Amend,
    Erase,
}

/// <summary>The person or system behind an access: never a shared account.</summary>
public readonly record struct Actor(string Type, string Id)
{
    public static Actor Applicant(Guid applicationId) => new("applicant", applicationId.ToString());

    public static Actor System(string service) => new(Roles.Service, service);

    public static Actor From(ClaimsPrincipal user) => new(
        user.FindFirst(AtlasClaims.Role)?.Value ?? "unknown",
        user.FindFirst(AtlasClaims.Subject)?.Value ?? "unknown");
}

public static class AccessLogExtensions
{
    public static ModelBuilder ConfigureAccessLog(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccessLogEntry>(entry =>
        {
            entry.ToTable("AccessLog");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.ActorType).HasMaxLength(20);
            entry.Property(e => e.ActorId).HasMaxLength(100);
            entry.Property(e => e.Action).HasMaxLength(10);
            entry.Property(e => e.Resource).HasMaxLength(40);
            entry.Property(e => e.ResourceId).HasMaxLength(64);
            entry.Property(e => e.TraceId).HasMaxLength(32);
            entry.HasIndex(e => e.ResourceId);
        });
        return modelBuilder;
    }

    /// <summary>
    /// Adds an access-log row. The caller's SaveChanges writes it together with the operation, so if the row can't be
    /// written the operation fails too: the log fails closed.
    /// </summary>
    public static void LogAccess(this DbContext db, Actor actor, AccessAction action, string resource, string resourceId, DateTimeOffset at) =>
        db.Set<AccessLogEntry>().Add(new AccessLogEntry
        {
            At = at,
            ActorType = actor.Type,
            ActorId = actor.Id,
            Action = action.ToString().ToLowerInvariant(),
            Resource = resource,
            ResourceId = resourceId,
            TraceId = Activity.Current?.TraceId.ToHexString(),
        });
}
