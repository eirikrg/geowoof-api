namespace GeoWoof.Modules.Identity.Contracts;

/// <summary>In-process API of the Identity module for other modules.</summary>
public interface IIdentityApi
{
    /// <summary>The account with that email, for sample data that belongs to known accounts.</summary>
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken);
}
