namespace Atlas.Shared.Contracts;

/// <summary>
/// What Onboarding sends Screening: the applicant as typed, and nothing else. Screening keeps it as the snapshot
/// that was screened.
/// </summary>
public sealed record ScreeningRequest(string FirstName, string LastName, DateOnly DateOfBirth, string Nationality, DateTimeOffset SubmittedAt);

public sealed record ScreeningResponse(Guid ApplicationId, string Status);

/// <summary>A screening's outcome. Only an officer turns <see cref="Referred"/> into either of the others.</summary>
public static class ScreeningStatus
{
    public const string Cleared = "CLEARED";
    public const string Referred = "REFERRED";
    public const string Rejected = "REJECTED";
}

public static class ScreeningRoutes
{
    public static string For(string market, Guid applicationId) => $"internal/v1/markets/{market}/screenings/{applicationId}";
}
