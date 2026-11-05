namespace GeoWoof.Modules.Pets.Contracts;

/// <summary>In-process API of the Pets module for other modules.</summary>
public interface IPetsApi
{
    /// <summary>The owner of a pet, or <c>null</c> if the pet does not exist.</summary>
    Task<Guid?> FindOwnerAsync(Guid petId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> GetPetIdsAsync(Guid ownerId, CancellationToken cancellationToken);
}
