using GeoWoof.Api.Extensions;
using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Infrastructure;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Infrastructure;
using GeoWoof.Modules.Identity.Presentation;
using GeoWoof.Modules.Media.Infrastructure;
using GeoWoof.Modules.Media.Presentation;
using GeoWoof.Modules.Pets.Infrastructure;
using GeoWoof.Modules.Pets.Presentation;
using GeoWoof.Modules.Social.Infrastructure;
using GeoWoof.Modules.Social.Presentation;
using GeoWoof.Modules.Walks.Infrastructure;
using GeoWoof.Modules.Walks.Presentation;

var builder = WebApplication.CreateBuilder(args);

builder.AddGeoWoofObservability();
builder.Services.AddGeoWoofApi(builder.Environment);
builder.Services.AddGeoWoofInfrastructure(builder.Environment);

// Modules (Infrastructure: AddXxxModule), then the handler decorators, applied once to every module.
builder.Services.AddIdentityModule();
builder.Services.AddMediaModule();
builder.Services.AddPetsModule();
builder.Services.AddWalksModule();
builder.Services.AddSocialModule();
builder.Services.DecorateApplicationHandlers();

var app = builder.Build();

app.UseGeoWoofPipeline();
app.MapGeoWoofHealthChecks();
app.MapGeoWoofDocumentation();

// Module endpoints (Presentation: MapXxxEndpoints) under the versioned group.
var api = app.MapGroup("/api/v1");
api.MapIdentityEndpoints();
api.MapMediaEndpoints();
api.MapPetsEndpoints();
api.MapWalksEndpoints();
api.MapSocialEndpoints();

await app.InitializeDatabasesAsync();
await app.RunAsync();

/// <summary>Entry point; public so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
