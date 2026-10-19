namespace Atlas.Onboarding.Applications;

/// <summary>
/// What the applicant sees. Neutral on purpose: <see cref="UnderReview"/> never says why, and a rejection never says
/// it came from screening.
/// </summary>
public static class CustomerStatus
{
    public const string Draft = "DRAFT";
    public const string InProgress = "IN_PROGRESS";
    public const string ActionRequired = "ACTION_REQUIRED";
    public const string UnderReview = "UNDER_REVIEW";
    public const string AwaitingBranchVisit = "AWAITING_BRANCH_VISIT";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";

    /// <summary>The applicant asked for erasure after submitting; the record is restricted until retention ends.</summary>
    public const string Withdrawn = "WITHDRAWN";

    public static string Of(Application application) => application.IsRestricted
        ? Withdrawn
        : application.State switch
        {
            ApplicationState.Draft => Draft,
            ApplicationState.Verifying or ApplicationState.Screening or ApplicationState.OpeningAccount => InProgress,
            ApplicationState.UnderReview => UnderReview,
            ApplicationState.ActionRequired => ActionRequired,
            ApplicationState.AwaitingBranchVisit => AwaitingBranchVisit,
            ApplicationState.Approved => Approved,
            _ => Rejected,
        };

    public static bool IsFinal(string status) => status is Approved or Rejected or Withdrawn;
}

/// <summary>What the app should do next, for every status.</summary>
public static class NextAction
{
    public const string CompleteDetails = "COMPLETE_DETAILS";
    public const string Submit = "SUBMIT";
    public const string RetakePhoto = "RETAKE_PHOTO";
    public const string Wait = "WAIT";
    public const string VisitBranch = "VISIT_BRANCH";
    public const string None = "NONE";
}

internal static class ApplicationViews
{
    public static ApplicationStatusResponse Status(Application application)
    {
        var status = CustomerStatus.Of(application);
        IReadOnlyList<string> missing = status == CustomerStatus.Draft ? ApplicationRules.Missing(application) : [];
        var (next, message) = status switch
        {
            CustomerStatus.Draft when missing.Count > 0 => (NextAction.CompleteDetails, "application.incomplete"),
            CustomerStatus.Draft => (NextAction.Submit, "application.readyToSubmit"),
            CustomerStatus.InProgress => (NextAction.Wait, "application.inProgress"),
            CustomerStatus.UnderReview => (NextAction.Wait, "application.underReview"),
            CustomerStatus.ActionRequired => (NextAction.RetakePhoto, "application.retakePhoto"),
            CustomerStatus.AwaitingBranchVisit => (NextAction.VisitBranch, "application.visitBranch"),
            CustomerStatus.Approved => (NextAction.None, "application.approved"),
            CustomerStatus.Rejected => (NextAction.None, "application.rejected"),
            _ => (NextAction.None, "application.withdrawn"),
        };
        return new ApplicationStatusResponse(application.Id, status, next, message, missing);
    }

    public static ApplicationCreatedResponse Created(Application application, string token)
    {
        var status = Status(application);
        return new ApplicationCreatedResponse(application.Id, token, status.Status, status.NextAction, status.MessageCode, status.MissingFields);
    }

    public static ApplicationDetailsResponse Details(Application application, string? identifierValue)
    {
        var status = Status(application);
        var applicant = application.Applicant;
        var identifier = applicant.IdentifierType is null || identifierValue is null
            ? null
            : new IdentifierView(applicant.IdentifierType, identifierValue, applicant.IdentifierIssuingCountry);

        return new ApplicationDetailsResponse(
            application.Id,
            status.Status,
            status.NextAction,
            status.MessageCode,
            status.MissingFields,
            application.CreatedAt,
            application.SubmittedAt,
            application.DecidedAt,
            new ApplicantView(
                applicant.FirstName,
                applicant.LastName,
                applicant.DateOfBirth,
                applicant.Nationality,
                applicant.CountryOfResidence,
                identifier,
                applicant.Email,
                applicant.Phone,
                applicant.TermsAcceptedAt is not null),
            application.Documents
                .OrderBy(d => d.CreatedAt)
                .Select(d => new DocumentView(d.Type, d.Version, d.State.ToString().ToUpperInvariant(), d.CreatedAt))
                .ToList());
    }

    /// <summary>The applicant sees the last four characters; branch staff checking the physical ID see it all.</summary>
    public static string Mask(string value) => value.Length <= 4 ? new string('*', value.Length) : new string('*', value.Length - 4) + value[^4..];
}
