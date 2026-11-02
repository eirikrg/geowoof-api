using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GeoWoof.IntegrationTests.Infrastructure;

/// <summary>Hosts the real API (Program.cs) against the test containers, in the <c>Testing</c> environment.</summary>
public sealed class ApiFactory(TestInfrastructure infrastructure, IReadOnlyDictionary<string, string?>? overrides = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Postgres"] = infrastructure.PostgresConnectionString,
            ["ConnectionStrings:Redis"] = infrastructure.RedisConnectionString,
            ["Storage:ServiceUrl"] = infrastructure.StorageUrl,
            ["Storage:AccessKey"] = infrastructure.StorageAccessKey,
            ["Storage:SecretKey"] = infrastructure.StorageSecretKey,
            ["Storage:Bucket"] = TestInfrastructure.StorageBucket,
            ["Storage:CreateBucketIfMissing"] = "true",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
