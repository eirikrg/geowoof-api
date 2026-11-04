using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Application;
using GeoWoof.Modules.Pets.Application.Abstractions;
using GeoWoof.Modules.Pets.Application.Events;
using GeoWoof.Modules.Pets.Application.Pets;
using GeoWoof.Modules.Pets.Application.Weights;
using GeoWoof.Modules.Pets.Domain;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.Pets.UnitTests;

public sealed class PetHandlerTests
{
    private static readonly Guid Ana = Guid.CreateVersion7();
    private static readonly Guid Bea = Guid.CreateVersion7();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
    private readonly InMemoryPets _pets = new();
    private readonly InMemoryWeights _weights = new();
    private readonly FakeMedia _media = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_a_pet_attaches_its_photo_for_the_owner()
    {
        var photo = Guid.CreateVersion7();

        var result = await Create().Handle(new CreatePetCommand(Ana, Input() with { AvatarAssetId = photo }), Token);

        result.Value.OwnerId.ShouldBe(Ana);
        result.Value.Avatar!.AssetId.ShouldBe(photo);
        _media.Attached.ShouldBe([(photo, Ana, MediaUsage.PetAvatar)]);
        _unitOfWork.Saves.ShouldBe(1);
    }

    [Fact]
    public async Task A_photo_that_cannot_be_attached_stops_the_creation()
    {
        _media.AttachFailure = new MediaAttachFailure("media.wrong_purpose", "Wrong", IsNotFound: false);

        var result = await Create().Handle(new CreatePetCommand(Ana, Input() with { AvatarAssetId = Guid.CreateVersion7() }), Token);

        result.Error.Code.ShouldBe("media.wrong_purpose");
        _pets.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_person_has_a_limited_number_of_pets()
    {
        for (var i = 0; i < Pet.MaxPerOwner; i++)
        {
            (await Create().Handle(new CreatePetCommand(Ana, Input()), Token)).IsSuccess.ShouldBeTrue();
        }

        var result = await Create().Handle(new CreatePetCommand(Ana, Input()), Token);

        result.Error.ShouldBe(PetsErrors.LimitReached);
        (await Create().Handle(new CreatePetCommand(Bea, Input()), Token)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_microchip_is_hidden_from_other_people()
    {
        var created = (await Create().Handle(new CreatePetCommand(Ana, Input() with { Microchip = "941000024567890" }), Token)).Value;
        var handler = new GetPetQueryHandler(_pets, _media);
        var list = new GetPetsQueryHandler(_pets, _media);

        (await handler.Handle(new GetPetQuery(Ana, created.Id), Token)).Value.Microchip.ShouldBe("941000024567890");
        (await handler.Handle(new GetPetQuery(Bea, created.Id), Token)).Value.Microchip.ShouldBeNull();
        (await list.Handle(new GetPetsQuery(Ana, null), Token)).Value.Items.ShouldHaveSingleItem().Microchip.ShouldBe("941000024567890");
        (await list.Handle(new GetPetsQuery(Bea, Ana), Token)).Value.Items.ShouldHaveSingleItem().Microchip.ShouldBeNull();
        (await list.Handle(new GetPetsQuery(Bea, null), Token)).Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_owner_can_change_a_pet()
    {
        var pet = (await Create().Handle(new CreatePetCommand(Ana, Input()), Token)).Value;

        (await Update().Handle(new UpdatePetCommand(Bea, pet.Id, Input() with { Name = "Mía" }), Token)).Error.ShouldBe(PetsErrors.NotOwner);
        (await Delete().Handle(new DeletePetCommand(Bea, pet.Id), Token)).Error.ShouldBe(PetsErrors.NotOwner);
        (await Update().Handle(new UpdatePetCommand(Ana, Guid.CreateVersion7(), Input()), Token)).Error.ShouldBe(PetsErrors.NotFound);

        (await Update().Handle(new UpdatePetCommand(Ana, pet.Id, Input() with { Name = "Lunita" }), Token)).Value.Name.ShouldBe("Lunita");
    }

    [Fact]
    public async Task Replacing_a_photo_releases_the_old_one()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var pet = (await Create().Handle(new CreatePetCommand(Ana, Input() with { AvatarAssetId = first }), Token)).Value;

        await Update().Handle(new UpdatePetCommand(Ana, pet.Id, Input() with { AvatarAssetId = second }), Token);

        _media.Attached.Select(attached => attached.AssetId).ShouldBe([first, second]);
        _media.Released.ShouldBe([first]);

        // Saving again with the same photo changes nothing.
        await Update().Handle(new UpdatePetCommand(Ana, pet.Id, Input() with { AvatarAssetId = second }), Token);
        _media.Attached.Count.ShouldBe(2);

        await Update().Handle(new UpdatePetCommand(Ana, pet.Id, Input()), Token);
        _media.Released.ShouldBe([first, second]);
    }

    [Fact]
    public async Task Deleting_a_pet_releases_its_photo()
    {
        var photo = Guid.CreateVersion7();
        var pet = (await Create().Handle(new CreatePetCommand(Ana, Input() with { AvatarAssetId = photo }), Token)).Value;

        (await Delete().Handle(new DeletePetCommand(Ana, pet.Id), Token)).IsSuccess.ShouldBeTrue();

        _pets.All.ShouldBeEmpty();
        _media.Released.ShouldBe([photo]);
    }

    [Fact]
    public async Task Deleting_an_account_deletes_only_its_pets()
    {
        await Create().Handle(new CreatePetCommand(Ana, Input()), Token);
        await Create().Handle(new CreatePetCommand(Ana, Input()), Token);
        var kept = (await Create().Handle(new CreatePetCommand(Bea, Input()), Token)).Value;

        await new DeletePetsOnUserDeleted(_pets).Handle(new UserDeleted(Ana, _time.GetUtcNow()), Token);

        _pets.All.ShouldHaveSingleItem().Id.ShouldBe(kept.Id);
    }

    [Fact]
    public async Task The_weight_history_is_private_to_the_owner()
    {
        var pet = (await Create().Handle(new CreatePetCommand(Ana, Input()), Token)).Value;

        (await new GetWeightsQueryHandler(_pets, _weights).Handle(new GetWeightsQuery(Bea, pet.Id, null, null), Token)).Error.ShouldBe(PetsErrors.NotOwner);
        (await AddWeight().Handle(new AddWeightCommand(Bea, pet.Id, new DateOnly(2026, 1, 1), 10), Token)).Error.ShouldBe(PetsErrors.NotOwner);
    }

    [Fact]
    public async Task Weights_are_paged_from_the_most_recent()
    {
        var pet = (await Create().Handle(new CreatePetCommand(Ana, Input()), Token)).Value;
        for (var day = 1; day <= 5; day++)
        {
            (await AddWeight().Handle(new AddWeightCommand(Ana, pet.Id, new DateOnly(2026, 1, day), 10 + day), Token)).IsSuccess.ShouldBeTrue();
        }

        var handler = new GetWeightsQueryHandler(_pets, _weights);
        var first = (await handler.Handle(new GetWeightsQuery(Ana, pet.Id, 2, null), Token)).Value;
        var second = (await handler.Handle(new GetWeightsQuery(Ana, pet.Id, 2, first.NextCursor), Token)).Value;
        var last = (await handler.Handle(new GetWeightsQuery(Ana, pet.Id, 2, second.NextCursor), Token)).Value;

        first.Items.Select(entry => entry.MeasuredOn.Day).ShouldBe([5, 4]);
        second.Items.Select(entry => entry.MeasuredOn.Day).ShouldBe([3, 2]);
        last.Items.Select(entry => entry.MeasuredOn.Day).ShouldBe([1]);
        last.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("bm90LWEtZGF0ZQ")]
    public async Task A_wrong_cursor_is_rejected(string cursor)
    {
        var pet = (await Create().Handle(new CreatePetCommand(Ana, Input()), Token)).Value;

        var result = await new GetWeightsQueryHandler(_pets, _weights).Handle(new GetWeightsQuery(Ana, pet.Id, null, cursor), Token);

        result.Error.ShouldBe(PetsErrors.InvalidCursor);
    }

    [Fact]
    public void The_page_size_stays_within_the_limits()
    {
        PageLimit.Normalize(null).ShouldBe(PageLimit.Default);
        PageLimit.Normalize(0).ShouldBe(1);
        PageLimit.Normalize(1000).ShouldBe(PageLimit.Max);
    }

    [Fact]
    public async Task Two_measures_on_one_day_are_a_conflict()
    {
        var pet = (await Create().Handle(new CreatePetCommand(Ana, Input()), Token)).Value;
        _unitOfWork.FailNextSaveWithDuplicate = true;

        var result = await AddWeight().Handle(new AddWeightCommand(Ana, pet.Id, new DateOnly(2026, 1, 1), 10), Token);

        result.Error.ShouldBe(PetsErrors.WeightDateTaken);
    }

    [Fact]
    public async Task A_weight_belongs_to_its_pet()
    {
        var pet = (await Create().Handle(new CreatePetCommand(Ana, Input()), Token)).Value;
        var other = (await Create().Handle(new CreatePetCommand(Ana, Input()), Token)).Value;
        var entry = (await AddWeight().Handle(new AddWeightCommand(Ana, pet.Id, new DateOnly(2026, 1, 1), 10), Token)).Value;

        var wrongPet = await new DeleteWeightCommandHandler(_pets, _weights, _unitOfWork).Handle(new DeleteWeightCommand(Ana, other.Id, entry.Id), Token);
        var corrected = await new UpdateWeightCommandHandler(_pets, _weights, _unitOfWork)
            .Handle(new UpdateWeightCommand(Ana, pet.Id, entry.Id, new DateOnly(2026, 1, 2), 11.5m), Token);

        wrongPet.Error.ShouldBe(PetsErrors.WeightNotFound);
        corrected.Value.WeightKg.ShouldBe(11.5m);
        corrected.Value.MeasuredOn.ShouldBe(new DateOnly(2026, 1, 2));
    }

    private CreatePetCommandHandler Create() => new(_pets, _media, _unitOfWork, _time);

    private UpdatePetCommandHandler Update() => new(_pets, _media, _unitOfWork, _time);

    private DeletePetCommandHandler Delete() => new(_pets, _media, _unitOfWork);

    private AddWeightCommandHandler AddWeight() => new(_pets, _weights, _unitOfWork, _time);

    private static PetInput Input() => new("Luna", "Dog", null, null, null, "Female", null, null, false, null, null, null);
}

internal sealed class InMemoryPets : IPetRepository
{
    private readonly List<Pet> _pets = [];

    public IReadOnlyList<Pet> All => _pets;

    public Task<Pet?> GetByIdAsync(Guid petId, CancellationToken cancellationToken) => Task.FromResult(_pets.Find(pet => pet.Id == petId));

    public Task<IReadOnlyList<Pet>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Pet>>(_pets.Where(pet => pet.OwnerId == ownerId).ToList());

    public Task<int> CountByOwnerAsync(Guid ownerId, CancellationToken cancellationToken) => Task.FromResult(_pets.Count(pet => pet.OwnerId == ownerId));

    public void Add(Pet pet) => _pets.Add(pet);

    public void Remove(Pet pet) => _pets.Remove(pet);
}

internal sealed class InMemoryWeights : IPetWeightRepository
{
    private readonly List<PetWeightEntry> _entries = [];

    public Task<PetWeightEntry?> GetByIdAsync(Guid petId, Guid entryId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.Find(entry => entry.PetId == petId && entry.Id == entryId));

    public Task<IReadOnlyList<PetWeightEntry>> GetPageAsync(Guid petId, DateOnly? before, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PetWeightEntry>>(_entries
            .Where(entry => entry.PetId == petId && (before is null || entry.MeasuredOn < before))
            .OrderByDescending(entry => entry.MeasuredOn)
            .Take(take)
            .ToList());

    public void Add(PetWeightEntry entry) => _entries.Add(entry);

    public void Remove(PetWeightEntry entry) => _entries.Remove(entry);
}

internal sealed class FakeMedia : IMediaApi
{
    public List<(Guid AssetId, Guid OwnerId, MediaUsage Usage)> Attached { get; } = [];

    public List<Guid> Released { get; } = [];

    public MediaAttachFailure? AttachFailure { get; set; }

    public Task<MediaAttachFailure?> AttachAsync(Guid assetId, Guid ownerId, MediaUsage usage, CancellationToken cancellationToken)
    {
        if (AttachFailure is null)
        {
            Attached.Add((assetId, ownerId, usage));
        }

        return Task.FromResult(AttachFailure);
    }

    public Task ReleaseAsync(Guid assetId, Guid ownerId, CancellationToken cancellationToken)
    {
        Released.Add(assetId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, MediaImage>> GetImagesAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, MediaImage>>(assetIds.ToDictionary(id => id, id => new MediaImage(id, "thumb", "medium")));

    public Task<Guid> ImportAsync(Guid ownerId, MediaUsage usage, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.CreateVersion7());
}

internal sealed class FakeUnitOfWork : IPetsUnitOfWork
{
    public int Saves { get; private set; }

    public bool FailNextSaveWithDuplicate { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSaveWithDuplicate)
        {
            FailNextSaveWithDuplicate = false;
            throw new UniqueConstraintViolationException("ix_pet_weight_entries", "Duplicate.", new InvalidOperationException());
        }

        Saves++;
        return Task.FromResult(1);
    }
}
