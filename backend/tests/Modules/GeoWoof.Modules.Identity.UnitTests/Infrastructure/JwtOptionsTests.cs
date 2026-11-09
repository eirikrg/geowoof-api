using GeoWoof.Modules.Identity.Infrastructure;
using GeoWoof.Modules.Identity.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GeoWoof.Modules.Identity.UnitTests.Infrastructure;

/// <summary>The key of the development settings is public: anyone could sign tokens for any account with it.</summary>
public sealed class JwtOptionsTests
{
    private const string PublicKey = JwtOptions.PublicDevelopmentKeyPrefix + "anything-after-it-long-enough";
    private const string OwnKey = "a-key-that-only-this-server-knows-0123456789";

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void The_public_development_key_does_not_start_outside_development(string environment)
    {
        using var provider = Services(environment, PublicKey);

        var error = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JwtOptions>>().Value);

        error.Message.ShouldContain("public");
    }

    [Theory]
    [InlineData("Development", PublicKey)]
    [InlineData("Production", OwnKey)]
    public void Development_may_use_it_and_anywhere_else_needs_its_own(string environment, string key)
    {
        using var provider = Services(environment, key);

        provider.GetRequiredService<IOptions<JwtOptions>>().Value.SigningKey.ShouldBe(key);
    }

    private static ServiceProvider Services(string environment, string key)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Jwt:SigningKey"] = key })
            .Build());
        services.AddSingleton<IHostEnvironment>(new Environment(environment));
        services.AddIdentityModule();
        return services.BuildServiceProvider();
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "GeoWoof.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
