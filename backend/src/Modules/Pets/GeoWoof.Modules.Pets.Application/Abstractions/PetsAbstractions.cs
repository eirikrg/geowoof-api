using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.Modules.Pets.Domain;

namespace GeoWoof.Modules.Pets.Application.Abstractions;

// Ports of the Pets application layer; the Infrastructure layer implements them.

public interface IPetRepository
{
    Task<Pet?> GetByIdAsync(Guid petId, CancellationToken cancellationToken);

    /// <summary>The pets of a person, oldest first.</summary>
    Task<IReadOnlyList<Pet>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Pet>> GetByIdsAsync(IReadOnlyCollection<Guid> petIds, CancellationToken cancellationToken);

    /// <summary>The pets that match the text, from the best match to the worst, after a position.</summary>
    Task<IReadOnlyList<RelevancePosition>> SearchAsync(string text, IReadOnlyCollection<Guid> excludedOwnerIds, RelevancePosition after, int take, CancellationToken cancellationToken);

    Task<int> CountByOwnerAsync(Guid ownerId, CancellationToken cancellationToken);

    void Add(Pet pet);

    /// <summary>Also removes its weight entries.</summary>
    void Remove(Pet pet);
}

public interface IPetWeightRepository
{
    Task<PetWeightEntry?> GetByIdAsync(Guid petId, Guid entryId, CancellationToken cancellationToken);

    /// <summary>Entries measured before <paramref name="before"/>, most recent first.</summary>
    Task<IReadOnlyList<PetWeightEntry>> GetPageAsync(Guid petId, DateOnly? before, int take, CancellationToken cancellationToken);

    void Add(PetWeightEntry entry);

    void Remove(PetWeightEntry entry);
}

/// <summary>Saves the Pets module's changes in one transaction (with its Outbox).</summary>
public interface IPetsUnitOfWork
{
    /// <exception cref="BuildingBlocks.Application.Persistence.UniqueConstraintViolationException">A unique value is already used.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
