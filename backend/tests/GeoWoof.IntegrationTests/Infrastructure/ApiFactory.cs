using System.Net.Http.Headers;
using GeoWoof.Modules.Identity.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GeoWoof.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API (Program.cs) against the test containers, in the <c>Testing</c> environment,
/// with migrations applied. Emails are captured and Google ID tokens are validated by a fake.
/// </summary>
public sealed class ApiFactory(TestInfrastructure infrastructure, IReadOnlyDictionary<string, string?>? overrides = null)
    : WebApplicationFactory<Program>
{
    public const string SigningKey = "integration-tests-signing-key-with-32-chars-or-more";

    public CapturingEmailSender Emails { get; } = new();

    public FakeGoogleValidator Google { get; } = new();

    /// <summary>An API on its own empty database, so tests never see each other's accounts.</summary>
    public static async Task<ApiFactory> WithFreshDatabaseAsync(TestInfrastructure infrastructure, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Postgres"] = await infrastructure.CreateDatabaseAsync(TestContext.Current.CancellationToken),
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return new ApiFactory(infrastructure, settings);
    }

    public HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

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
            ["Database:ApplyMigrationsOnStartup"] = "true",
            ["Auth:Jwt:SigningKey"] = SigningKey,
            ["Email:SmtpHost"] = "smtp.invalid",
            ["Api:AuthPermitsPerMinute"] = "10000",
            ["Api:RefreshPermitsPerMinute"] = "10000",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
            services.RemoveAll<IGoogleIdTokenValidator>();
            services.AddSingleton<IGoogleIdTokenValidator>(Google);
        });
    }
}

/// <summary>Keeps every email the API sends, to read the one-time codes.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToList();
            }
        }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }

    /// <summary>The 6-digit code of the last email sent to <paramref name="to"/>.</summary>
    public string LastCodeFor(string to) =>
        Sent.Last(message => string.Equals(message.To, to, StringComparison.OrdinalIgnoreCase)).Subject[^6..];
}

/// <summary>Google ID token validation without Google: tests register the tokens they will present.</summary>
public sealed class FakeGoogleValidator : IGoogleIdTokenValidator
{
    private readonly Dictionary<string, GoogleIdentity> _tokens = new(StringComparer.Ordinal);

    public bool IsEnabled { get; set; }

    public void Accept(string idToken, GoogleIdentity identity)
    {
        IsEnabled = true;
        _tokens[idToken] = identity;
    }

    public Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken cancellationToken) =>
        Task.FromResult(_tokens.GetValueOrDefault(idToken));
}
