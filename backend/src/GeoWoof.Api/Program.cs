using GeoWoof.Api.Extensions;
using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Infrastructure;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddGeoWoofObservability();
builder.Services.AddGeoWoofApi(builder.Environment);
builder.Services.AddGeoWoofInfrastructure(builder.Environment);

// Modules are registered here (Infrastructure: AddXxxModule), then the handler decorators are applied once.
builder.Services.DecorateApplicationHandlers();

var app = builder.Build();

app.UseGeoWoofPipeline();
app.MapGeoWoofHealthChecks();
app.MapGeoWoofDocumentation();

// Module endpoints are mapped here (Presentation: MapXxxEndpoints) under the /api/v1 group.
await app.InitializeDatabasesAsync();
await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
