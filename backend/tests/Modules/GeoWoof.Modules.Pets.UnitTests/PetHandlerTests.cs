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
using GeoWoof.Modules.Social.Contracts;
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
    private readonly FakeRelationships _relationships = new();
    private readonly FakeIdentity _identity = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<PetDto> CreateNamedAsync(Guid owner, string name)
    {
        var created = await Create().Handle(new CreatePetCommand(owner, Input() with { Name = name }), Token);
        return created.Value;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_block_in_either_direction_hides_the_pets(bool anaBlocks)
    {
        var luna = await CreateNamedAsync(Ana, "Luna");
        _identity.Add(Ana, "ana");
        _relationships.Block(anaBlocks ? Ana : Bea, anaBlocks ? Bea : Ana);

        (await new GetPetQueryHandler(_pets, _relationships, _media).Handle(new GetPetQuery(Bea, luna.Id), Token)).Error.ShouldBe(PetsErrors.NotFound);
        (await new GetUserPetsQueryHandler(_pets, _identity, _relationships, _media).Handle(new GetUserPetsQuery(Bea, Ana), Token)).Error.ShouldBe(PetsErrors.UserNotFound);
        (await new GetPetQueryHandler(_pets, _relationships, _media).Handle(new GetPetQuery(Ana, luna.Id), Token)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_pets_of_someone_who_does_not_exist_are_not_found()
    {
        (await new GetUserPetsQueryHandler(_pets, _identity, _relationships, _media).Handle(new GetUserPetsQuery(Bea, Ana), Token)).Error.ShouldBe(PetsErrors.UserNotFound);
    }

    [Fact]
    public async Task The_search_shows_the_owner_and_leaves_out_the_viewer_and_the_blocked()
    {
        var carlos = Guid.CreateVersion7();
        var dani = Guid.CreateVersion7();
        var visible = await CreateNamedAsync(Ana, "Luna");
        await CreateNamedAsync(Bea, "Luna");
        await CreateNamedAsync(carlos, "Luna");
        await CreateNamedAsync(dani, "Luna");
        _identity.Add(Ana, "ana");
        _identity.Add(Bea, "bea");
        _relationships.Block(Bea, carlos);
        _relationships.Block(dani, Bea);

        var result = await new SearchPetsQueryHandler(_pets, _identity, _relationships, _media).Handle(new SearchPetsQuery(Bea, " Luna ", null, null), Token);

        var found = result.Value.Items.Single();
        found.Id.ShouldBe(visible.Id);
        found.Owner.Username.ShouldBe("ana");
    }

    [Fact]
    public async Task The_search_leaves_out_pets_whose_owner_is_being_deleted()
    {
        await CreateNamedAsync(Ana, "Luna");

        var result = await new SearchPetsQueryHandler(_pets, _identity, _relationships, _media).Handle(new SearchPetsQuery(Bea, "Luna", null, null), Token);

        result.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_search_goes_through_pages_and_rejects_what_is_not_valid()
    {
        await CreateNamedAsync(Ana, "Luna");
        await CreateNamedAsync(Ana, "Luna");
        _identity.Add(Ana, "ana");
        var handler = new SearchPetsQueryHandler(_pets, _identity, _relationships, _media);

        var first = await handler.Handle(new SearchPetsQuery(Bea, "Luna", 1, null), Token);
        var second = await handler.Handle(new SearchPetsQuery(Bea, "Luna", 1, first.Value.NextCursor), Token);

        first.Value.NextCursor.ShouldNotBeNull();
        second.Value.NextCursor.ShouldBeNull();
        second.Value.Items.Single().Id.ShouldNotBe(first.Value.Items.Single().Id);
        (await handler.Handle(new SearchPetsQuery(Bea, "Luna", null, "!!"), Token)).Error.ShouldBe(PetsErrors.InvalidCursor);
        new SearchPetsQueryValidator().Validate(new SearchPetsQuery(Bea, "a", null, null)).IsValid.ShouldBeFalse();
        new SearchPetsQueryValidator().Validate(new SearchPetsQuery(Bea, new string('a', 51), null, null)).IsValid.ShouldBeFalse();
    }

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
        var handler = new GetPetQueryHandler(_pets, _relationships, _media);
        var list = new GetPetsQueryHandler(_pets, _media);
        var ofUser = new GetUserPetsQueryHandler(_pets, _identity, _relationships, _media);
        _identity.Add(Ana, "ana");

        (await handler.Handle(new GetPetQuery(Ana, created.Id), Token)).Value.Microchip.ShouldBe("941000024567890");
        (await handler.Handle(new GetPetQuery(Bea, created.Id), Token)).Value.Microchip.ShouldBeNull();
        (await list.Handle(new GetPetsQuery(Ana), Token)).Value.Items.ShouldHaveSingleItem().Microchip.ShouldBe("941000024567890");
        (await ofUser.Handle(new GetUserPetsQuery(Bea, Ana), Token)).Value.Items.ShouldHaveSingleItem().Microchip.ShouldBeNull();
        (await list.Handle(new GetPetsQuery(Bea), Token)).Value.Items.ShouldBeEmpty();
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

    public Task<IReadOnlyList<Pet>> GetByIdsAsync(IReadOnlyCollection<Guid> petIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Pet>>(_pets.Where(pet => petIds.Contains(pet.Id)).ToList());

    /// <summary>The real search is SQL and has its own integration tests: this finds pets by exact name, in order.</summary>
    public Task<IReadOnlyList<RelevancePosition>> SearchAsync(string text, IReadOnlyCollection<Guid> excludedOwnerIds, RelevancePosition after, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RelevancePosition>>(_pets
            .Where(pet => pet.Name == text && !excludedOwnerIds.Contains(pet.OwnerId))
            .Select(pet => new RelevancePosition(1, pet.Id))
            .OrderByDescending(hit => hit.Id)
            .Where(hit => hit.Id.CompareTo(after.Id) < 0 || after.Id == Guid.Empty)
            .Take(take)
            .ToList());

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

/// <summary>Who is related to whom, in the tests of a module that only knows the contract of Social.</summary>
internal sealed class FakeRelationships : IRelationshipsApi
{
    private readonly HashSet<(Guid, Guid)> _friends = [];
    private readonly HashSet<(Guid, Guid)> _blocks = [];

    public void Befriend(Guid first, Guid second)
    {
        _friends.Add((first, second));
        _friends.Add((second, first));
    }

    public void Block(Guid blocker, Guid blocked) => _blocks.Add((blocker, blocked));

    public Task<Relationship> GetRelationshipAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken)
    {
        if (viewerId == targetId)
        {
            return Task.FromResult(Relationship.Self);
        }

        if (_blocks.Contains((viewerId, targetId)) || _blocks.Contains((targetId, viewerId)))
        {
            return Task.FromResult(Relationship.Blocked);
        }

        return Task.FromResult(_friends.Contains((viewerId, targetId)) ? Relationship.Friend : Relationship.None);
    }

    public Task<IReadOnlyCollection<Guid>> GetHiddenUserIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> hidden = _blocks
            .Where(block => block.Item1 == userId || block.Item2 == userId)
            .Select(block => block.Item1 == userId ? block.Item2 : block.Item1)
            .Distinct()
            .ToList();
        return Task.FromResult(hidden);
    }
}

internal sealed class FakeIdentity : IIdentityApi
{
    public Dictionary<Guid, UserSummary> Users { get; } = [];

    public void Add(Guid id, string username) => Users[id] = new UserSummary(id, username, username, null, null);

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<IReadOnlyDictionary<Guid, UserSummary>> GetPublicUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, UserSummary> found = userIds.Distinct().Where(Users.ContainsKey).ToDictionary(id => id, id => Users[id]);
        return Task.FromResult(found);
    }
}
