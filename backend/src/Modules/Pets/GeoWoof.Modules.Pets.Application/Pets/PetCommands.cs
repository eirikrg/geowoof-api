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

/// <summary>The viewer's own pets.</summary>
public sealed record GetPetsQuery(Guid ViewerId) : IQuery<PagedResponse<PetDto>>;

internal sealed class GetPetsQueryHandler(IPetRepository pets, IMediaApi media) : IQueryHandler<GetPetsQuery, PagedResponse<PetDto>>
{
    public async Task<Result<PagedResponse<PetDto>>> Handle(GetPetsQuery query, CancellationToken cancellationToken)
    {
        var found = await pets.GetByOwnerAsync(query.ViewerId, cancellationToken);
        return new PagedResponse<PetDto>(await PetMapper.ToDtosAsync(found, query.ViewerId, media, cancellationToken), NextCursor: null);
    }
}

/// <summary>The pets of someone else. With a block in either direction, that person does not exist.</summary>
public sealed record GetUserPetsQuery(Guid ViewerId, Guid UserId) : IQuery<PagedResponse<PetDto>>;

internal sealed class GetUserPetsQueryHandler(IPetRepository pets, IIdentityApi identity, IRelationshipsApi relationships, IMediaApi media)
    : IQueryHandler<GetUserPetsQuery, PagedResponse<PetDto>>
{
    public async Task<Result<PagedResponse<PetDto>>> Handle(GetUserPetsQuery query, CancellationToken cancellationToken)
    {
        var relationship = await relationships.GetRelationshipAsync(query.ViewerId, query.UserId, cancellationToken);
        var users = await identity.GetPublicUsersAsync([query.UserId], cancellationToken);
        if (relationship == Relationship.Blocked || !users.ContainsKey(query.UserId))
        {
            return PetsErrors.UserNotFound;
        }

        var found = await pets.GetByOwnerAsync(query.UserId, cancellationToken);
        return new PagedResponse<PetDto>(await PetMapper.ToDtosAsync(found, query.ViewerId, media, cancellationToken), NextCursor: null);
    }
}

public sealed record GetPetQuery(Guid ViewerId, Guid PetId) : IQuery<PetDto>;

internal sealed class GetPetQueryHandler(IPetRepository pets, IRelationshipsApi relationships, IMediaApi media) : IQueryHandler<GetPetQuery, PetDto>
{
    public async Task<Result<PetDto>> Handle(GetPetQuery query, CancellationToken cancellationToken)
    {
        var pet = await pets.GetByIdAsync(query.PetId, cancellationToken);
        if (pet is null || await relationships.GetRelationshipAsync(query.ViewerId, pet.OwnerId, cancellationToken) == Relationship.Blocked)
        {
            return PetsErrors.NotFound;
        }

        return await PetMapper.ToDtoAsync(pet, query.ViewerId, media, cancellationToken);
    }
}

public sealed record CreatePetCommand(Guid OwnerId, PetInput Input) : ICommand<PetDto>;

internal sealed class CreatePetCommandValidator : AbstractValidator<CreatePetCommand>
{
    public CreatePetCommandValidator(TimeProvider timeProvider) =>
        RuleFor(command => command.Input)
            .SetValidator(new PetInputValidator(timeProvider))

            // The API reports the fields of the request (`name`), not of this command (`input.name`).
            .OverridePropertyName(string.Empty);
}

internal sealed class CreatePetCommandHandler(IPetRepository pets, IMediaApi media, IPetsUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<CreatePetCommand, PetDto>
{
    public async Task<Result<PetDto>> Handle(CreatePetCommand command, CancellationToken cancellationToken)
    {
        if (await pets.CountByOwnerAsync(command.OwnerId, cancellationToken) >= Pet.MaxPerOwner)
        {
            return PetsErrors.LimitReached;
        }

        if (command.Input.AvatarAssetId is { } avatar)
        {
            var failure = await media.AttachAsync(avatar, command.OwnerId, MediaUsage.PetAvatar, cancellationToken);
            if (failure is not null)
            {
                return failure.ToError();
            }
        }

        var now = timeProvider.GetUtcNow();
        var pet = Pet.Create(Guid.CreateVersion7(now), command.OwnerId, command.Input.ToDetails(), command.Input.AvatarAssetId, now);
        pets.Add(pet);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await PetMapper.ToDtoAsync(pet, command.OwnerId, media, cancellationToken);
    }
}

public sealed record UpdatePetCommand(Guid UserId, Guid PetId, PetInput Input) : ICommand<PetDto>;

internal sealed class UpdatePetCommandValidator : AbstractValidator<UpdatePetCommand>
{
    public UpdatePetCommandValidator(TimeProvider timeProvider) =>
        RuleFor(command => command.Input)
            .SetValidator(new PetInputValidator(timeProvider))

            // The API reports the fields of the request (`name`), not of this command (`input.name`).
            .OverridePropertyName(string.Empty);
}

internal sealed class UpdatePetCommandHandler(IPetRepository pets, IMediaApi media, IPetsUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<UpdatePetCommand, PetDto>
{
    public async Task<Result<PetDto>> Handle(UpdatePetCommand command, CancellationToken cancellationToken)
    {
        var pet = await pets.GetByIdAsync(command.PetId, cancellationToken);
        if (pet is null)
        {
            return PetsErrors.NotFound;
        }

        if (!pet.IsOwnedBy(command.UserId))
        {
            return PetsErrors.NotOwner;
        }

        var previousAvatar = pet.AvatarAssetId;
        var newAvatar = command.Input.AvatarAssetId;
        if (newAvatar is { } avatar && avatar != previousAvatar)
        {
            var failure = await media.AttachAsync(avatar, pet.OwnerId, MediaUsage.PetAvatar, cancellationToken);
            if (failure is not null)
            {
                return failure.ToError();
            }
        }

        pet.Update(command.Input.ToDetails(), newAvatar, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (previousAvatar is { } replaced && replaced != newAvatar)
        {
            await media.ReleaseAsync(replaced, pet.OwnerId, cancellationToken);
        }

        return await PetMapper.ToDtoAsync(pet, command.UserId, media, cancellationToken);
    }
}

public sealed record DeletePetCommand(Guid UserId, Guid PetId) : ICommand;

internal sealed class DeletePetCommandHandler(IPetRepository pets, IMediaApi media, IPetsUnitOfWork unitOfWork) : ICommandHandler<DeletePetCommand>
{
    public async Task<Result> Handle(DeletePetCommand command, CancellationToken cancellationToken)
    {
        var pet = await pets.GetByIdAsync(command.PetId, cancellationToken);
        if (pet is null)
        {
            return PetsErrors.NotFound;
        }

        if (!pet.IsOwnedBy(command.UserId))
        {
            return PetsErrors.NotOwner;
        }

        pet.Delete();
        pets.Remove(pet);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (pet.AvatarAssetId is { } avatar)
        {
            await media.ReleaseAsync(avatar, pet.OwnerId, cancellationToken);
        }

        return Result.Success();
    }
}

internal static class MediaAttachFailureExtensions
{
    public static Error ToError(this MediaAttachFailure failure) =>
        failure.IsNotFound ? Error.NotFound(failure.Code, failure.Message) : Error.Unprocessable(failure.Code, failure.Message);
}
