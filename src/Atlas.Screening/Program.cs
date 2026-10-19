using Atlas.Screening.Persistence;
using Atlas.Screening.Reviews;
using Atlas.Screening.Screenings;
using Atlas.Shared.Hosting;
using Atlas.Shared.Http;
using Atlas.Shared.Persistence;

var builder = AtlasHost.CreateBuilder(args, "screening");
builder.Services.AddMarketDatabases<ScreeningDbContext>(builder.Configuration, "screening", options => new ScreeningDbContext(options));
builder.Services.AddOutboundClient(WorldCheckClient.Name, "Screening:WorldCheck:BaseUrl", builder.Configuration);
builder.Services.AddSingleton<WorldCheckClient>();

var app = builder.Build();
app.UseAtlasDefaults();
app.MapScreenings();
app.MapReviews();

await app.MigrateMarketDatabasesAsync<ScreeningDbContext>();
await app.RunAsync();
