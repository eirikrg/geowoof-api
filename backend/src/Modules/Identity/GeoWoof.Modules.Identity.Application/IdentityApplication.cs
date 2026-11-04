using GeoWoof.BuildingBlocks.Application;
using GeoWoof.Modules.Identity.Application.EmailVerification;
using GeoWoof.Modules.Identity.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace GeoWoof.Modules.Identity.Application;

public static class IdentityApplication
{
    /// <summary>Handlers, validators and application services of the Identity module.</summary>
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        services.AddApplicationHandlers(typeof(IdentityApplication).Assembly);
        services.AddScoped<SessionService>();
        services.AddScoped<VerificationCodeService>();
        services.AddScoped<IdentityEmailDispatcher>();
        return services;
    }
}
