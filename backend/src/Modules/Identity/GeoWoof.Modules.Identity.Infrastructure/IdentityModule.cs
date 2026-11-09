using GeoWoof.BuildingBlocks.Application.Security;
using GeoWoof.BuildingBlocks.Infrastructure.Events;
using GeoWoof.BuildingBlocks.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Application;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Identity.Infrastructure.External;
using GeoWoof.Modules.Identity.Infrastructure.Persistence;
using GeoWoof.Modules.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GeoWoof.Modules.Identity.Infrastructure;

public static class IdentityModule
{
    /// <summary>
    /// Registers the Identity module and the JWT bearer authentication used by every module
    /// (the Identity module issues the tokens, so it owns their validation parameters too).
    /// </summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddOptions<AuthSettings>().BindConfiguration(AuthSettings.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateDataAnnotations()
            .Validate<IHostEnvironment>(
                (jwt, environment) => JwtOptions.IsAllowedIn(environment, jwt.SigningKey),
                "Auth:Jwt:SigningKey is the development key, which is public. Set a key of your own.")
            .ValidateOnStart();
        services.AddOptions<GoogleAuthOptions>().BindConfiguration(GoogleAuthOptions.SectionName);
        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();

        services.AddIdentityApplication();

        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.SchemaName);
        services.AddScoped<IIdentityUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<IdentityDbContext>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IVerificationCodeRepository, VerificationCodeRepository>();
        services.AddScoped<IDevelopmentDataSeeder, IdentityDevelopmentSeeder>();
        services.AddIntegrationEvents(typeof(UserDeleted).Assembly);

        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton<ISecretGenerator, SecretGenerator>();
        services.AddSingleton<IGoogleIdTokenValidator, GoogleIdTokenValidator>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.CreateSigningKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = GeoWoofClaimTypes.Subject,
                };

                // WebSockets cannot send headers from the app: the token of a hub comes in the URL.
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"].ToString();
                        if (token.Length > 0 && context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        return services;
    }
}
