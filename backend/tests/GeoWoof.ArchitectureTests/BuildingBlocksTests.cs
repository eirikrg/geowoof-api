using GeoWoof.BuildingBlocks.Application;
using GeoWoof.BuildingBlocks.Contracts;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.BuildingBlocks.Presentation.Results;
using NetArchTest.Rules;

namespace GeoWoof.ArchitectureTests;

/// <summary>The shared kernel follows the same layering as the modules.</summary>
public sealed class BuildingBlocksTests
{
    [Fact]
    public void Rule_engine_detects_real_dependencies()
    {
        // Guards against vacuous rules: ModuleDbContext does derive from EF Core's DbContext.
        Types.InAssembly(typeof(ModuleDbContext).Assembly)
            .That().HaveName(nameof(ModuleDbContext))
            .Should().HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .ShouldPass("The dependency analysis must see real type references.");
    }

    [Fact]
    public void Domain_does_not_depend_on_frameworks_or_outer_layers()
    {
        Types.InAssembly(typeof(Result).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny([
                .. Solution.InfrastructureFrameworks,
                "GeoWoof.BuildingBlocks.Application",
                "GeoWoof.BuildingBlocks.Infrastructure",
                "GeoWoof.BuildingBlocks.Presentation",
                "GeoWoof.BuildingBlocks.Contracts",
            ])
            .ShouldPass("BuildingBlocks.Domain is the dependency-free shared kernel.");
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure()
    {
        Types.InAssembly(typeof(ApplicationServiceCollectionExtensions).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny([
                .. Solution.InfrastructureFrameworks,
                "GeoWoof.BuildingBlocks.Infrastructure",
                "GeoWoof.BuildingBlocks.Presentation",
            ])
            .ShouldPass("BuildingBlocks.Application must stay free of EF Core, ASP.NET Core and infrastructure SDKs.");
    }

    [Fact]
    public void Contracts_only_depend_on_the_base_library()
    {
        Types.InAssembly(typeof(IIntegrationEvent).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny([.. Solution.InfrastructureFrameworks, "GeoWoof.BuildingBlocks.Domain", "GeoWoof.BuildingBlocks.Application"])
            .ShouldPass("Contracts are shared with every module and must not leak other layers.");
    }

    [Fact]
    public void Presentation_does_not_depend_on_infrastructure()
    {
        Types.InAssembly(typeof(ResultHttpExtensions).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(["GeoWoof.BuildingBlocks.Infrastructure", "Microsoft.EntityFrameworkCore", "Npgsql", "StackExchange.Redis", "Amazon"])
            .ShouldPass("Presentation only talks to Application.");
    }
}
