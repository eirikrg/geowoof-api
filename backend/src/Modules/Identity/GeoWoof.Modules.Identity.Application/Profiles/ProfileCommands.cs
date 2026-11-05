using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Users;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Contracts;

namespace GeoWoof.Modules.Identity.Application.Profiles;

/// <summary>The public face of an account. It never includes the email.</summary>
public sealed record ProfileDto(
    Guid Id,
    string? Username,
    string DisplayName,
    string? Bio,
    string? City,
    MediaImage? Avatar,
    bool IsPrivate,
    DateTimeOffset CreatedAt);

/// <summary>Builds <see cref="ProfileDto"/>s with the signed URLs of the avatar.</summary>
internal static class ProfileMapper
{
    /// <param name="user">The account.</param>
    /// <param name="media">Signs the URLs.</param>
    /// <param name="isRestricted">What a private profile shows to those who do not follow it (docs/api.md, phase 5).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<ProfileDto> ToDtoAsync(User user, IMediaApi media, CancellationToken cancellationToken, bool isRestricted = false)
    {
        MediaImage? avatar = null;
        if (user.AvatarAssetId is { } assetId)
        {
            var images = await media.GetImagesAsync([assetId], cancellationToken);
            avatar = images.GetValueOrDefault(assetId);
        }

        return new ProfileDto(
            user.Id,
            user.Username?.Value,
            isRestricted ? user.Username!.Value : user.DisplayName,
            user.Bio,
            isRestricted ? null : user.City,
            avatar,
            user.IsPrivate,
            user.CreatedAt);
    }
}

public sealed record GetMyProfileQuery(Guid UserId) : IQuery<ProfileDto>;

internal sealed class GetMyProfileQueryHandler(IUserRepository users, IMediaApi media) : IQueryHandler<GetMyProfileQuery, ProfileDto>
{
    public async Task<Result<ProfileDto>> Handle(GetMyProfileQuery query, CancellationToken cancellationToken) =>
        await users.GetByIdAsync(query.UserId, cancellationToken) is { } user
            ? await ProfileMapper.ToDtoAsync(user, media, cancellationToken)
            : IdentityErrors.AccountNotFound;
}

/// <summary>
/// Accounts that have not finished the onboarding have no public profile yet. With a block in
/// either direction, the answer is the same as for an account that does not exist.
/// </summary>
public sealed record GetPublicProfileQuery(Guid ViewerId, Guid UserId) : IQuery<ProfileDto>;

internal sealed class GetPublicProfileQueryHandler(IUserRepository users, IRelationshipsApi relationships, IMediaApi media) : IQueryHandler<GetPublicProfileQuery, ProfileDto>
{
    public async Task<Result<ProfileDto>> Handle(GetPublicProfileQuery query, CancellationToken cancellationToken)
    {
        var relationship = await relationships.GetRelationshipAsync(query.ViewerId, query.UserId, cancellationToken);
        if (relationship == Relationship.Blocked)
        {
            return IdentityErrors.UserNotFound;
        }

        return await users.GetByIdAsync(query.UserId, cancellationToken) is { HasCompletedOnboarding: true } user
            ? await ProfileMapper.ToDtoAsync(user, media, cancellationToken, isRestricted: user.IsPrivate && relationship == Relationship.None)
            : IdentityErrors.UserNotFound;
    }
}

/// <summary>Replaces the profile: a <c>null</c> field is cleared.</summary>
public sealed record ReplaceMyProfileCommand(Guid UserId, string DisplayName, string? Bio, string? City, Guid? AvatarAssetId) : ICommand<ProfileDto>;

internal sealed class ReplaceMyProfileCommandValidator : AbstractValidator<ReplaceMyProfileCommand>
{
    public ReplaceMyProfileCommandValidator()
    {
        RuleFor(command => command.DisplayName).ValidDisplayName();
        RuleFor(command => command.Bio).MaximumLength(User.BioMaxLength);
        RuleFor(command => command.City).MaximumLength(User.CityMaxLength);
    }
}

internal sealed class ReplaceMyProfileCommandHandler(IUserRepository users, IMediaApi media, IIdentityUnitOfWork unitOfWork)
    : ICommandHandler<ReplaceMyProfileCommand, ProfileDto>
{
    public async Task<Result<ProfileDto>> Handle(ReplaceMyProfileCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        var previousAvatar = user.AvatarAssetId;
        if (command.AvatarAssetId is { } newAvatar && newAvatar != previousAvatar)
        {
            var failure = await media.AttachAsync(newAvatar, user.Id, MediaUsage.UserAvatar, cancellationToken);
            if (failure is not null)
            {
                return failure.IsNotFound ? Error.NotFound(failure.Code, failure.Message) : Error.Unprocessable(failure.Code, failure.Message);
            }
        }

        user.UpdateProfile(command.DisplayName, command.Bio, command.City, command.AvatarAssetId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (previousAvatar is { } replaced && replaced != command.AvatarAssetId)
        {
            await media.ReleaseAsync(replaced, user.Id, cancellationToken);
        }

        return await ProfileMapper.ToDtoAsync(user, media, cancellationToken);
    }
}
