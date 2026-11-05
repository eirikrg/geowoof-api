namespace GeoWoof.Modules.Pets.Contracts;

/// <summary>What another module shows of a pet.</summary>
public sealed record PetSummary(Guid Id, string Name, Guid? AvatarAssetId);

/// <summary>In-process API of the Pets module for other modules.</summary>
public interface IPetsApi
{
    /// <summary>The owner of a pet, or <c>null</c> if the pet does not exist.</summary>
    Task<Guid?> FindOwnerAsync(Guid petId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> GetPetIdsAsync(Guid ownerId, CancellationToken cancellationToken);

    /// <summary>Those pets that exist. Only call it for pets the current user is allowed to see.</summary>
    Task<IReadOnlyDictionary<Guid, PetSummary>> GetPetsAsync(IReadOnlyCollection<Guid> petIds, CancellationToken cancellationToken);
}
