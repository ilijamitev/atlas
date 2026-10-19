namespace Atlas.Onboarding.Workflow;

/// <summary>Where a push notification would go. It only logs for now, with the status and no personal data.</summary>
public interface IApplicantNotifier
{
    void StatusChanged(string market, Guid applicationId, string status);
}

internal sealed class LoggingApplicantNotifier(ILogger<LoggingApplicantNotifier> logger) : IApplicantNotifier
{
    public void StatusChanged(string market, Guid applicationId, string status) =>
        logger.LogInformation("Notify applicant of {ApplicationId} in {Market}: {Status}", applicationId, market, status);
}
