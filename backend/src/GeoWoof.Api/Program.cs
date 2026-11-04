using GeoWoof.Api.Extensions;
using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Infrastructure;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Infrastructure;
using GeoWoof.Modules.Identity.Presentation;

var builder = WebApplication.CreateBuilder(args);

builder.AddGeoWoofObservability();
builder.Services.AddGeoWoofApi(builder.Environment);
builder.Services.AddGeoWoofInfrastructure(builder.Environment);

// Modules (Infrastructure: AddXxxModule), then the handler decorators, applied once to every module.
builder.Services.AddIdentityModule();
builder.Services.DecorateApplicationHandlers();

var app = builder.Build();

app.UseGeoWoofPipeline();
app.MapGeoWoofHealthChecks();
app.MapGeoWoofDocumentation();

// Module endpoints (Presentation: MapXxxEndpoints) under the versioned group.
var api = app.MapGroup("/api/v1");
api.MapIdentityEndpoints();

await app.InitializeDatabasesAsync();
await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
