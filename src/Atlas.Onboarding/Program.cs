using Atlas.Onboarding;
using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Documents;
using Atlas.Onboarding.Persistence;
using Atlas.Onboarding.Workflow;
using Atlas.Shared.Encryption;
using Atlas.Shared.Hosting;
using Atlas.Shared.Persistence;

var builder = AtlasHost.CreateBuilder(args, "onboarding");
builder.Services.AddMarketDatabases<OnboardingDbContext>(builder.Configuration, "onboarding", options => new OnboardingDbContext(options));
builder.Services.AddFieldEncryption(builder.Configuration);
builder.Services.AddDocumentStorage(builder.Configuration);
builder.Services.AddApplicantAuthentication();
builder.Services.AddCreateRateLimit();
builder.Services.AddOnboarding(builder.Configuration);

// The ticket's images arrive as base64 in JSON, several MB each. The endpoints enforce the limit too, with a clearer error.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("Onboarding:MaxRequestBytes") is { } limit
        ? limit + 1024
        : new OnboardingOptions().MaxRequestBytes + 1024);

var app = builder.Build();
app.UseAtlasDefaults();
app.UseRateLimiter();
app.MapApplications();

await app.MigrateMarketDatabasesAsync<OnboardingDbContext>();
await app.EnsureDocumentContainersAsync();
await app.RunAsync();
