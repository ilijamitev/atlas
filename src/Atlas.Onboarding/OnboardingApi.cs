namespace Atlas.Onboarding;

/// <summary>
/// Marks this assembly for WebApplicationFactory. The integration tests host all three services in one process,
/// and each service's top-level Program class is in the global namespace, so they need a distinct type each.
/// </summary>
public sealed class OnboardingApi;
