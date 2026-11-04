using GeoWoof.BuildingBlocks.Domain.Entities;

namespace GeoWoof.Modules.Pets.Domain;

public enum PetSpecies
{
    Dog = 0,
    Cat = 1,
    Other = 2,
}

public enum PetSex
{
    Unknown = 0,
    Male = 1,
    Female = 2,
}

public enum PetSize
{
    Small = 0,
    Medium = 1,
    Large = 2,
    Giant = 3,
}

public sealed record PetCreatedDomainEvent(Guid PetId, Guid OwnerId, string Name) : IDomainEvent;

public sealed record PetUpdatedDomainEvent(Guid PetId, Guid OwnerId, string Name) : IDomainEvent;

public sealed record PetDeletedDomainEvent(Guid PetId, Guid OwnerId) : IDomainEvent;

/// <summary>Everything the owner can edit about a pet, except its photo.</summary>
public sealed record PetDetails(
    string Name,
    PetSpecies Species,
    string? SpeciesOther,
    string? Breed,
    DateOnly? BirthDate,
    PetSex Sex,
    PetSize? Size,
    string? Color,
    bool Neutered,
    string? Microchip,
    string? Bio);

/// <summary>A pet and its owner's data about it. The microchip is private: see <see cref="MicrochipVisibleTo"/>.</summary>
public sealed class Pet : AggregateRoot
{
    public const int MaxPerOwner = 20;
    public const int NameMaxLength = 50;
    public const int SpeciesOtherMaxLength = 50;
    public const int BreedMaxLength = 80;
    public const int ColorMaxLength = 50;
    public const int MicrochipMaxLength = 32;
    public const int BioMaxLength = 500;

    /// <summary>No date about a pet can be older than this.</summary>
    public static readonly DateOnly EarliestDate = new(1990, 1, 1);

    /// <summary>Constructor for EF Core.</summary>
    private Pet()
    {
        Name = string.Empty;
    }

    private Pet(Guid id, Guid ownerId, DateTimeOffset now)
        : base(id)
    {
        OwnerId = ownerId;
        CreatedAt = now;
        Name = string.Empty;
    }

    public Guid OwnerId { get; private set; }

    public string Name { get; private set; }

    public PetSpecies Species { get; private set; }

    public string? SpeciesOther { get; private set; }

    public string? Breed { get; private set; }

    public DateOnly? BirthDate { get; private set; }

    public PetSex Sex { get; private set; }

    public PetSize? Size { get; private set; }

    public string? Color { get; private set; }

    public bool Neutered { get; private set; }

    public string? Microchip { get; private set; }

    /// <summary>Image in the Media module.</summary>
    public Guid? AvatarAssetId { get; private set; }

    public string? Bio { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Pet Create(Guid id, Guid ownerId, PetDetails details, Guid? avatarAssetId, DateTimeOffset now)
    {
        var pet = new Pet(id, ownerId, now);
        pet.Apply(details, avatarAssetId, now);
        pet.Raise(new PetCreatedDomainEvent(pet.Id, pet.OwnerId, pet.Name));
        return pet;
    }

    public void Update(PetDetails details, Guid? avatarAssetId, DateTimeOffset now)
    {
        Apply(details, avatarAssetId, now);
        Raise(new PetUpdatedDomainEvent(Id, OwnerId, Name));
    }

    /// <summary>Marks the pet for deletion; the repository removes it in the same unit of work.</summary>
    public void Delete() => Raise(new PetDeletedDomainEvent(Id, OwnerId));

    public bool IsOwnedBy(Guid userId) => OwnerId == userId;

    /// <summary>The microchip identifies the pet and its owner: nobody else may ever see it.</summary>
    public string? MicrochipVisibleTo(Guid viewerId) => IsOwnedBy(viewerId) ? Microchip : null;

    private void Apply(PetDetails details, Guid? avatarAssetId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrWhiteSpace(details.Name);
        Name = details.Name.Trim();
        Species = details.Species;
        SpeciesOther = details.Species == PetSpecies.Other ? Clean(details.SpeciesOther) : null;
        Breed = Clean(details.Breed);
        BirthDate = details.BirthDate;
        Sex = details.Sex;
        Size = details.Size;
        Color = Clean(details.Color);
        Neutered = details.Neutered;
        Microchip = Clean(details.Microchip);
        AvatarAssetId = avatarAssetId;
        Bio = Clean(details.Bio);
        UpdatedAt = now;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One weight measurement of a pet. There is at most one per day.</summary>
public sealed class PetWeightEntry : Entity
{
    public const decimal MaxWeightKg = 200;

    /// <summary>Constructor for EF Core.</summary>
    private PetWeightEntry()
    {
    }

    private PetWeightEntry(Guid id, Guid petId, DateOnly measuredOn, decimal weightKg)
        : base(id)
    {
        PetId = petId;
        MeasuredOn = measuredOn;
        WeightKg = weightKg;
    }

    public Guid PetId { get; private set; }

    public DateOnly MeasuredOn { get; private set; }

    public decimal WeightKg { get; private set; }

    public static bool IsValidWeight(decimal weightKg) =>
        weightKg is > 0 and <= MaxWeightKg && decimal.Round(weightKg, 2) == weightKg;

    public static PetWeightEntry Record(Guid id, Guid petId, DateOnly measuredOn, decimal weightKg)
    {
        if (!IsValidWeight(weightKg))
        {
            throw new ArgumentOutOfRangeException(nameof(weightKg), weightKg, "The weight is not valid.");
        }

        return new PetWeightEntry(id, petId, measuredOn, weightKg);
    }

    public void Correct(DateOnly measuredOn, decimal weightKg)
    {
        if (!IsValidWeight(weightKg))
        {
            throw new ArgumentOutOfRangeException(nameof(weightKg), weightKg, "The weight is not valid.");
        }

        MeasuredOn = measuredOn;
        WeightKg = weightKg;
    }
}
