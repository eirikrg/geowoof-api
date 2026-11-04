using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.Application;

internal sealed class IdentityApi(IUserRepository users) : IIdentityApi
{
    public async Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) =>
        Email.IsValid(email) ? (await users.GetByEmailAsync(Email.Create(email), cancellationToken))?.Id : null;
}
