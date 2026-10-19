namespace Atlas.Onboarding.Applications;

public static class DocumentTypes
{
    public const string Passport = "PASSPORT";
    public const string IdCard = "ID_CARD";
    public const string Selfie = "SELFIE";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Passport, IdCard, Selfie]);

    public static bool IsIdentity(string type) => type is Passport or IdCard;
}

public enum DocumentState
{
    /// <summary>The row exists; the image may not have reached blob storage yet.</summary>
    Pending,

    /// <summary>The encrypted image is in the market's container.</summary>
    Stored,
}

/// <summary>
/// One uploaded image's metadata. The image itself is an encrypted blob named {applicationId}/{documentId}, so the
/// name carries no personal data. Every version is kept (GC-2026-0814 #4: "all supporting documents").
/// </summary>
public sealed class Document
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public string Type { get; set; } = "";
    public int Version { get; set; }
    public string BlobName { get; set; } = "";
    public int SizeBytes { get; set; }
    public byte[] Sha256 { get; set; } = [];
    public DocumentState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
