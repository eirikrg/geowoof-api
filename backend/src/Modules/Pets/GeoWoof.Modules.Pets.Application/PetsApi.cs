using GeoWoof.Modules.Pets.Application.Abstractions;
using GeoWoof.Modules.Pets.Contracts;

namespace GeoWoof.Modules.Pets.Application;

internal sealed class PetsApi(IPetRepository pets) : IPetsApi
{
    public async Task<Guid?> FindOwnerAsync(Guid petId, CancellationToken cancellationToken) =>
        (await pets.GetByIdAsync(petId, cancellationToken))?.OwnerId;

    public async Task<IReadOnlyList<Guid>> GetPetIdsAsync(Guid ownerId, CancellationToken cancellationToken) =>
        (await pets.GetByOwnerAsync(ownerId, cancellationToken)).Select(pet => pet.Id).ToList();

    public async Task<IReadOnlyDictionary<Guid, PetSummary>> GetPetsAsync(IReadOnlyCollection<Guid> petIds, CancellationToken cancellationToken) =>
        petIds.Count == 0
            ? new Dictionary<Guid, PetSummary>()
            : (await pets.GetByIdsAsync(petIds, cancellationToken)).ToDictionary(pet => pet.Id, pet => new PetSummary(pet.Id, pet.Name, pet.AvatarAssetId));
}
