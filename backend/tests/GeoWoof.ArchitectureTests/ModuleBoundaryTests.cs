using System.Reflection;
using System.Xml.Linq;
using GeoWoof.BuildingBlocks.Contracts;
using NetArchTest.Rules;

namespace GeoWoof.ArchitectureTests;

/// <summary>
/// Layer and module rules from docs/architecture.md §3.3. They apply automatically to every
/// <c>GeoWoof.Modules.*</c> assembly referenced by this project.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly string[] SourceFolders = ["src", "tests"];

    [Fact]
    public void Every_module_project_is_referenced_by_the_architecture_tests()
    {
        var modulesDirectory = new DirectoryInfo(Path.Combine(Solution.BackendRoot.FullName, "src", "Modules"));
        var moduleProjects = modulesDirectory.Exists
            ? modulesDirectory.EnumerateFiles("*.csproj", SearchOption.AllDirectories).Select(file => Path.GetFileNameWithoutExtension(file.Name)).ToList()
            : [];
        var referenced = Solution.Modules.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);

        moduleProjects.Where(project => !referenced.Contains(project))
            .ShouldBeEmpty("Add the missing module projects to GeoWoof.ArchitectureTests.csproj so their rules are checked.");
    }

    [Fact]
    public void Every_project_is_part_of_the_solution()
    {
        var root = Solution.BackendRoot.FullName;
        var solution = XDocument.Load(Path.Combine(root, "GeoWoof.slnx"));
        var listed = solution.Descendants("Project")
            .Select(project => Path.GetFileNameWithoutExtension(project.Attribute("Path")!.Value))
            .ToHashSet(StringComparer.Ordinal);
        var onDisk = SourceFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension);

        onDisk.Where(project => !listed.Contains(project!)).ShouldBeEmpty("Add the missing projects to GeoWoof.slnx.");
    }

    [Fact]
    public void Module_projects_follow_the_layer_naming()
    {
        Solution.Modules
            .Where(assembly => assembly.GetName().Name!.Split('.').Length != 4 || !Solution.ModuleLayers.Contains(Solution.LayerOf(assembly)))
            .Select(assembly => assembly.GetName().Name)
            .ShouldBeEmpty("Module assemblies are named GeoWoof.Modules.<Module>.<Domain|Application|Infrastructure|Presentation|Contracts>.");
    }

    [Fact]
    public void Domain_layers_only_depend_on_the_shared_kernel()
    {
        foreach (var assembly in Solution.Layer("Domain"))
        {
            var module = Solution.ModuleOf(assembly);
            Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny([
                    .. Solution.InfrastructureFrameworks,
                    "GeoWoof.BuildingBlocks.Application",
                    "GeoWoof.BuildingBlocks.Infrastructure",
                    "GeoWoof.BuildingBlocks.Presentation",
                    .. OwnLayers(module, "Application", "Infrastructure", "Presentation", "Contracts"),
                ])
                .ShouldPass($"{assembly.GetName().Name} must only depend on BuildingBlocks.Domain.");
        }
    }

    [Fact]
    public void Application_layers_do_not_depend_on_infrastructure_or_presentation()
    {
        foreach (var assembly in Solution.Layer("Application"))
        {
            var module = Solution.ModuleOf(assembly);
            Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny([
                    .. Solution.InfrastructureFrameworks,
                    "GeoWoof.BuildingBlocks.Infrastructure",
                    "GeoWoof.BuildingBlocks.Presentation",
                    .. OwnLayers(module, "Infrastructure", "Presentation"),
                ])
                .ShouldPass($"{assembly.GetName().Name} must not reference EF Core, ASP.NET Core or its outer layers.");
        }
    }

    [Fact]
    public void Presentation_layers_only_talk_to_application()
    {
        foreach (var assembly in Solution.Layer("Presentation"))
        {
            var module = Solution.ModuleOf(assembly);
            Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny([
                    "Microsoft.EntityFrameworkCore",
                    "Npgsql",
                    "StackExchange.Redis",
                    "Amazon",
                    "GeoWoof.BuildingBlocks.Infrastructure",
                    .. OwnLayers(module, "Domain", "Infrastructure"),
                ])
                .ShouldPass($"{assembly.GetName().Name} must not see domain entities or infrastructure.");
        }
    }

    [Fact]
    public void Contracts_do_not_depend_on_other_layers()
    {
        foreach (var assembly in Solution.Layer("Contracts"))
        {
            var module = Solution.ModuleOf(assembly);
            Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny([
                    .. Solution.InfrastructureFrameworks,
                    "GeoWoof.BuildingBlocks.Domain",
                    "GeoWoof.BuildingBlocks.Application",
                    "GeoWoof.BuildingBlocks.Infrastructure",
                    "GeoWoof.BuildingBlocks.Presentation",
                    .. OwnLayers(module, "Domain", "Application", "Infrastructure", "Presentation"),
                ])
                .ShouldPass($"{assembly.GetName().Name} is public API: primitives and contract DTOs only.");
        }
    }

    [Fact]
    public void Modules_only_reference_other_modules_through_their_contracts()
    {
        var modules = Solution.ModuleNames.ToList();
        foreach (var assembly in Solution.Modules)
        {
            var module = Solution.ModuleOf(assembly);
            var forbidden = modules
                .Where(other => !string.Equals(other, module, StringComparison.Ordinal))
                .SelectMany(other => OwnLayers(other, "Domain", "Application", "Infrastructure", "Presentation"))
                .ToArray();
            if (forbidden.Length == 0)
            {
                continue;
            }

            Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .ShouldPass($"{assembly.GetName().Name} may only use other modules' Contracts.");
        }
    }

    [Fact]
    public void Domain_and_application_classes_are_sealed_or_abstract()
    {
        foreach (var assembly in Solution.Layer("Domain").Concat(Solution.Layer("Application")))
        {
            Types.InAssembly(assembly)
                .That().AreClasses().And().AreNotAbstract()
                .Should().BeSealed()
                .ShouldPass($"Classes in {assembly.GetName().Name} must be sealed (or abstract base classes).");
        }
    }

    [Fact]
    public void Integration_events_declare_a_stable_event_type()
    {
        var missing = Solution.Layer("Contracts")
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(type))
            .Where(type => type.GetCustomAttribute<EventTypeAttribute>() is null)
            .Select(type => type.FullName);

        missing.ShouldBeEmpty("Every integration event needs [EventType(\"module.event_name.vN\")].");
    }

    private static string[] OwnLayers(string module, params string[] layers) =>
        layers.Select(layer => $"{Solution.ModulesNamespace}.{module}.{layer}").ToArray();
}
