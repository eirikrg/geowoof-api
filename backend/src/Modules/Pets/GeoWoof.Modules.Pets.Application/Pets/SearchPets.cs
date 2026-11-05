using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Application.Abstractions;
using GeoWoof.Modules.Pets.Domain;
using GeoWoof.Modules.Social.Contracts;

namespace GeoWoof.Modules.Pets.Application.Pets;

public sealed record PetOwnerDto(Guid Id, string Username, string DisplayName, string? City, MediaImage? Avatar);

public sealed record PetSearchResultDto(Guid Id, string Name, string Species, string? SpeciesOther, string? Breed, MediaImage? Avatar, PetOwnerDto Owner);

/// <summary>Finds pets by name or breed, without the viewer's own and without those of anyone blocked in either direction.</summary>
public sealed record SearchPetsQuery(Guid ViewerId, string Q, int? Limit, string? Cursor) : IQuery<PagedResponse<PetSearchResultDto>>;

internal sealed class SearchPetsQueryValidator : AbstractValidator<SearchPetsQuery>
{
    public const int MinLength = 2;
    public const int MaxLength = 50;

    public SearchPetsQueryValidator() =>
        RuleFor(query => query.Q)
            .Must(text => text.Trim().Length is >= MinLength and <= MaxLength)
            .WithMessage($"Write between {MinLength} and {MaxLength} characters.");
}

internal sealed class SearchPetsQueryHandler(IPetRepository pets, IIdentityApi identity, IRelationshipsApi relationships, IMediaApi media)
    : IQueryHandler<SearchPetsQuery, PagedResponse<PetSearchResultDto>>
{
    public async Task<Result<PagedResponse<PetSearchResultDto>>> Handle(SearchPetsQuery query, CancellationToken cancellationToken)
    {
        var after = RelevancePosition.Start;
        if (query.Cursor is { } cursor)
        {
            if (RelevanceCursor.Decode(cursor) is not { } position)
            {
                return PetsErrors.InvalidCursor;
            }

            after = position;
        }

        var hidden = await relationships.GetHiddenUserIdsAsync(query.ViewerId, cancellationToken);
        var limit = PageLimit.Normalize(query.Limit);
        var hits = await pets.SearchAsync(query.Q.Trim(), [.. hidden, query.ViewerId], after, limit + 1, cancellationToken);
        var page = hits.Take(limit).ToList();
        var next = hits.Count > limit ? RelevanceCursor.Encode(page[^1]) : null;

        var found = (await pets.GetByIdsAsync(page.Select(hit => hit.Id).ToList(), cancellationToken)).ToDictionary(pet => pet.Id);
        var owners = await identity.GetPublicUsersAsync(found.Values.Select(pet => pet.OwnerId).Distinct().ToList(), cancellationToken);

        // The pets of a private profile are only for those who follow it. Being a few, they are left out here
        // and not in the query, which does not know who has a private profile: a page can come with fewer results.
        var following = await relationships.GetFollowingIdsAsync(query.ViewerId, cancellationToken);
        foreach (var privateOwner in owners.Values.Where(owner => owner.IsPrivate && !following.Contains(owner.Id)).ToList())
        {
            found = found.Where(pair => pair.Value.OwnerId != privateOwner.Id).ToDictionary();
        }

        var assetIds = found.Values.Select(pet => pet.AvatarAssetId).Concat(owners.Values.Select(owner => owner.AvatarAssetId)).OfType<Guid>().ToList();
        var images = await media.GetImagesAsync(assetIds, cancellationToken);

        var items = page
            .Where(hit => found.TryGetValue(hit.Id, out var pet) && owners.ContainsKey(pet.OwnerId))
            .Select(hit => found[hit.Id])
            .Select(pet => ToDto(pet, owners[pet.OwnerId], images))
            .ToList();
        return new PagedResponse<PetSearchResultDto>(items, next);
    }

    private static PetSearchResultDto ToDto(Pet pet, UserSummary owner, IReadOnlyDictionary<Guid, MediaImage> images) => new(
        pet.Id,
        pet.Name,
        pet.Species.ToString(),
        pet.SpeciesOther,
        pet.Breed,
        pet.AvatarAssetId is { } petAsset ? images.GetValueOrDefault(petAsset) : null,
        new PetOwnerDto(
            owner.Id,
            owner.Username,
            owner.DisplayName,
            owner.City,
            owner.AvatarAssetId is { } ownerAsset ? images.GetValueOrDefault(ownerAsset) : null));
}
