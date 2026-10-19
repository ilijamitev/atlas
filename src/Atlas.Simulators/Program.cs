using Atlas.Shared.Hosting;
using Atlas.Simulators;

var builder = AtlasHost.CreateBuilder(args, "simulators");
builder.Services.AddSimulators(builder.Configuration);

var app = builder.Build();
app.UseAtlasDefaults();
app.MapSimulators();

await app.RunAsync();
