using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Clients;
using Atlas.Onboarding.Documents;
using Atlas.Shared.Auth;
using Atlas.Shared.Http;

namespace Atlas.Onboarding.Workflow;

internal static class WorkflowExtensions
{
    public static IServiceCollection AddOnboarding(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OnboardingOptions>().Bind(configuration.GetSection("Onboarding"));

        services.AddOutboundClient(IdNowClient.Name, "Onboarding:IdNow:BaseUrl", configuration);
        services.AddOutboundClient(ScreeningClient.Name, "Onboarding:Screening:BaseUrl", configuration).WithServiceToken();
        services.AddOutboundClient(CoreBankingClient.Name, "Onboarding:CoreBanking:BaseUrl", configuration);
        services.AddSingleton<IdNowClient>();
        services.AddSingleton<ScreeningClient>();
        services.AddSingleton<CoreBankingClient>();

        services.AddSingleton<ApplicationService>();
        services.AddSingleton<DecisionWaiter>();
        services.AddSingleton<WorkerSignal>();
        services.AddSingleton<WorkClaimer>();
        services.AddSingleton<ApplicationSteps>();
        services.AddSingleton<DocumentSweeper>();
        services.AddSingleton<IApplicantNotifier, LoggingApplicantNotifier>();
        services.AddHostedService<ApplicationWorker>();
        return services;
    }
}
