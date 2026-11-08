using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(GeoWoof.IntegrationTests.Infrastructure.TestInfrastructure))]

namespace GeoWoof.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL + PostGIS, Redis and S3-compatible storage (SeaweedFS), started once per test run with
/// Testcontainers. Each can be replaced by an already running service through an environment variable:
/// <c>GEOWOOF_TEST_POSTGRES</c> (admin connection string), <c>GEOWOOF_TEST_REDIS</c> and
/// <c>GEOWOOF_TEST_STORAGE_URL</c> (with <c>GEOWOOF_TEST_STORAGE_ACCESS_KEY</c>/<c>_SECRET_KEY</c>).
/// </summary>
public sealed class TestInfrastructure : IAsyncLifetime
{
    public const string PostgresImage = "postgis/postgis:18-3.6";
    public const string RedisImage = "redis:8.8";
    public const string StorageImage = "chrislusf/seaweedfs:4.52";
    public const string StorageBucket = "geowoof-test-media";
    private const int StoragePort = 8333;

    private PostgreSqlContainer? _postgres;
    private RedisContainer? _redis;
    private IContainer? _storage;

    public string PostgresConnectionString { get; private set; } = string.Empty;

    public string RedisConnectionString { get; private set; } = string.Empty;

    public string StorageUrl { get; private set; } = string.Empty;

    public string StorageAccessKey { get; private set; } = "geowoof";

    public string StorageSecretKey { get; private set; } = "geowoof_test_secret";

    public async ValueTask InitializeAsync()
    {
        var startups = new List<Task>();

        PostgresConnectionString = Environment.GetEnvironmentVariable("GEOWOOF_TEST_POSTGRES") ?? string.Empty;
        if (PostgresConnectionString.Length == 0)
        {
            // Each test has its own API, with an Outbox per module, and the classes run at the same time: the 100
            // connections of Postgres by default fall short.
            _postgres = new PostgreSqlBuilder(PostgresImage).WithCommand("-c", "max_connections=300").Build();
            startups.Add(_postgres.StartAsync());
        }

        RedisConnectionString = Environment.GetEnvironmentVariable("GEOWOOF_TEST_REDIS") ?? string.Empty;
        if (RedisConnectionString.Length == 0)
        {
            _redis = new RedisBuilder(RedisImage).Build();
            startups.Add(_redis.StartAsync());
        }

        StorageUrl = Environment.GetEnvironmentVariable("GEOWOOF_TEST_STORAGE_URL") ?? string.Empty;
        if (StorageUrl.Length == 0)
        {
            _storage = new ContainerBuilder(StorageImage)
                .WithCommand("server", "-s3", $"-s3.port={StoragePort}", "-s3.config=/etc/seaweedfs/s3.json")
                .WithResourceMapping(Encoding.UTF8.GetBytes(StorageIdentities()), "/etc/seaweedfs/s3.json")
                .WithPortBinding(StoragePort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                    .ForPort(StoragePort)
                    .ForPath("/")
                    .ForStatusCodeMatching(status => (int)status < 500)))
                .Build();
            startups.Add(_storage.StartAsync());
        }
        else
        {
            StorageAccessKey = Environment.GetEnvironmentVariable("GEOWOOF_TEST_STORAGE_ACCESS_KEY") ?? StorageAccessKey;
            StorageSecretKey = Environment.GetEnvironmentVariable("GEOWOOF_TEST_STORAGE_SECRET_KEY") ?? StorageSecretKey;
        }

        await Task.WhenAll(startups);

        if (_postgres is not null)
        {
            PostgresConnectionString = _postgres.GetConnectionString();
        }

        if (_redis is not null)
        {
            RedisConnectionString = _redis.GetConnectionString();
        }

        if (_storage is not null)
        {
            StorageUrl = $"http://{_storage.Hostname}:{_storage.GetMappedPublicPort(StoragePort)}";
        }
    }

    /// <summary>Creates an empty database so that a test class gets an isolated schema set.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(PostgresConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            // The database name is generated above, never user input.
#pragma warning disable CA2100
            command.CommandText = $"CREATE DATABASE \"{name}\"";
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(PostgresConnectionString) { Database = name }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_storage is not null)
        {
            await _storage.DisposeAsync();
        }
    }

    private string StorageIdentities() =>
        $$"""
        {
          "identities": [
            {
              "name": "geowoof",
              "credentials": [{ "accessKey": "{{StorageAccessKey}}", "secretKey": "{{StorageSecretKey}}" }],
              "actions": ["Admin", "Read", "List", "Tagging", "Write"]
            }
          ]
        }
        """;
}
