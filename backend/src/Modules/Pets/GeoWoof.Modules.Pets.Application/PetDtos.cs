using FluentValidation;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Domain;

namespace GeoWoof.Modules.Pets.Application;

public sealed record PetDto(
    Guid Id,
    Guid OwnerId,
    string Name,
    string Species,
    string? SpeciesOther,
    string? Breed,
    DateOnly? BirthDate,
    string Sex,
    string? Size,
    string? Color,
    bool Neutered,
    string? Microchip,
    MediaImage? Avatar,
    string? Bio,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WeightEntryDto(Guid Id, DateOnly MeasuredOn, decimal WeightKg)
{
    internal static WeightEntryDto From(PetWeightEntry entry) => new(entry.Id, entry.MeasuredOn, entry.WeightKg);
}

/// <summary>The editable fields of a pet as they arrive from the API. Enums are validated strings.</summary>
public sealed record PetInput(
    string Name,
    string Species,
    string? SpeciesOther,
    string? Breed,
    DateOnly? BirthDate,
    string Sex,
    string? Size,
    string? Color,
    bool Neutered,
    string? Microchip,
    Guid? AvatarAssetId,
    string? Bio)
{
    internal PetDetails ToDetails() => new(
        Name,
        Enum.Parse<PetSpecies>(Species),
        SpeciesOther,
        Breed,
        BirthDate,
        Enum.Parse<PetSex>(Sex),
        Size is null ? null : Enum.Parse<PetSize>(Size),
        Color,
        Neutered,
        Microchip,
        Bio);
}

/// <summary>Builds <see cref="PetDto"/>s for one viewer: signed image URLs, and the microchip only for the owner.</summary>
internal static class PetMapper
{
    public static async Task<PetDto> ToDtoAsync(Pet pet, Guid viewerId, IMediaApi media, CancellationToken cancellationToken) =>
        (await ToDtosAsync([pet], viewerId, media, cancellationToken))[0];

    public static async Task<IReadOnlyList<PetDto>> ToDtosAsync(IReadOnlyList<Pet> pets, Guid viewerId, IMediaApi media, CancellationToken cancellationToken)
    {
        var assetIds = pets.Where(pet => pet.AvatarAssetId is not null).Select(pet => pet.AvatarAssetId!.Value).ToList();
        var images = await media.GetImagesAsync(assetIds, cancellationToken);
        return pets.Select(pet => ToDto(pet, viewerId, images)).ToList();
    }

    private static PetDto ToDto(Pet pet, Guid viewerId, IReadOnlyDictionary<Guid, MediaImage> images) => new(
        pet.Id,
        pet.OwnerId,
        pet.Name,
        pet.Species.ToString(),
        pet.SpeciesOther,
        pet.Breed,
        pet.BirthDate,
        pet.Sex.ToString(),
        pet.Size?.ToString(),
        pet.Color,
        pet.Neutered,
        pet.MicrochipVisibleTo(viewerId),
        pet.AvatarAssetId is { } assetId ? images.GetValueOrDefault(assetId) : null,
        pet.Bio,
        pet.CreatedAt,
        pet.UpdatedAt);
}

/// <summary>Field rules shared by the Pets validators.</summary>
internal static class PetValidationRules
{
    /// <summary>
    /// Dates are local to the user and the server only knows UTC. One extra day keeps "today" valid
    /// in time zones that are ahead of UTC.
    /// </summary>
    public static bool IsValidPastDate(DateOnly date, TimeProvider timeProvider) =>
        date >= Pet.EarliestDate && date <= DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(1);

    public static IRuleBuilderOptions<T, string> ValidEnum<T, TEnum>(this IRuleBuilder<T, string> rule)
        where TEnum : struct, Enum =>
        rule.Must(value => Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal))
            .WithMessage($"Use one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
}

internal sealed class PetInputValidator : AbstractValidator<PetInput>
{
    public PetInputValidator(TimeProvider timeProvider)
    {
        RuleFor(input => input.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= Pet.NameMaxLength)
            .WithMessage($"The name must have 1 to {Pet.NameMaxLength} characters.");
        RuleFor(input => input.Species).ValidEnum<PetInput, PetSpecies>();
        RuleFor(input => input.SpeciesOther)
            .Must(value => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= Pet.SpeciesOtherMaxLength)
            .When(input => string.Equals(input.Species, nameof(PetSpecies.Other), StringComparison.Ordinal))
            .WithMessage($"Say which species it is, in at most {Pet.SpeciesOtherMaxLength} characters.");
        RuleFor(input => input.SpeciesOther)
            .Null()
            .When(input => !string.Equals(input.Species, nameof(PetSpecies.Other), StringComparison.Ordinal))
            .WithMessage("Only the species Other has this field.");
        RuleFor(input => input.Breed).MaximumLength(Pet.BreedMaxLength);
        RuleFor(input => input.BirthDate)
            .Must(date => date is null || PetValidationRules.IsValidPastDate(date.Value, timeProvider))
            .WithMessage("The birth date cannot be in the future or before 1990.");
        RuleFor(input => input.Sex).ValidEnum<PetInput, PetSex>();
        RuleFor(input => input.Size!).ValidEnum<PetInput, PetSize>().When(input => input.Size is not null);
        RuleFor(input => input.Color).MaximumLength(Pet.ColorMaxLength);
        RuleFor(input => input.Microchip)
            .Must(value => string.IsNullOrWhiteSpace(value)
                || (value.Trim().Length <= Pet.MicrochipMaxLength && value.Trim().All(char.IsAsciiLetterOrDigit)))
            .WithMessage($"The microchip has at most {Pet.MicrochipMaxLength} letters and digits.");
        RuleFor(input => input.Bio).MaximumLength(Pet.BioMaxLength);
    }
}
