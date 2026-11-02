using System.Reflection;
using NetArchTest.Rules;

namespace GeoWoof.ArchitectureTests;

/// <summary>Locates the solution on disk and the GeoWoof assemblies referenced by this test project.</summary>
internal static class Solution
{
    public const string ModulesNamespace = "GeoWoof.Modules";

    /// <summary>Layer suffixes of a module, in the order of the architecture rules.</summary>
    public static readonly string[] ModuleLayers = ["Domain", "Application", "Infrastructure", "Presentation", "Contracts"];

    /// <summary>Third-party frameworks that inner layers must never depend on.</summary>
    public static readonly string[] InfrastructureFrameworks =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
        "StackExchange.Redis",
        "Amazon",
    ];

    private static readonly Lazy<DirectoryInfo> Root = new(FindRoot);
    private static readonly Lazy<List<Assembly>> ModuleAssemblies = new(LoadModuleAssemblies);

    public static DirectoryInfo BackendRoot => Root.Value;

    public static IReadOnlyList<Assembly> Modules => ModuleAssemblies.Value;

    /// <summary>Module name (for example <c>Pets</c>) of an assembly named <c>GeoWoof.Modules.Pets.Domain</c>.</summary>
    public static string ModuleOf(Assembly assembly) => assembly.GetName().Name!.Split('.')[2];

    public static string LayerOf(Assembly assembly) => assembly.GetName().Name!.Split('.')[^1];

    public static IEnumerable<string> ModuleNames => Modules.Select(ModuleOf).Distinct(StringComparer.Ordinal);

    public static IEnumerable<Assembly> Layer(string layer) =>
        Modules.Where(assembly => string.Equals(LayerOf(assembly), layer, StringComparison.Ordinal));

    /// <summary>Asserts a NetArchTest rule and lists the offending types in the failure message.</summary>
    public static void ShouldPass(this ConditionList rule, string because)
    {
        var result = rule.GetResult();
        result.IsSuccessful.ShouldBeTrue($"{because} Offending types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    private static DirectoryInfo FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GeoWoof.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Could not find GeoWoof.slnx above the test output directory.");
    }

    private static List<Assembly> LoadModuleAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, ModulesNamespace + ".*.dll")
            .Select(path => Assembly.LoadFrom(path))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .ToList();
}
