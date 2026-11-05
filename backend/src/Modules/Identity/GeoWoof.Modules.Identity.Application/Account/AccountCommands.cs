using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Identity.Application.Abstractions;
using GeoWoof.Modules.Identity.Domain;
using GeoWoof.Modules.Identity.Domain.Users;

namespace GeoWoof.Modules.Identity.Application.Account;

public sealed record GetCurrentUserQuery(Guid UserId) : IQuery<UserDto>;

internal sealed class GetCurrentUserQueryHandler(IUserRepository users) : IQueryHandler<GetCurrentUserQuery, UserDto>
{
    public async Task<Result<UserDto>> Handle(GetCurrentUserQuery query, CancellationToken cancellationToken) =>
        await users.GetByIdAsync(query.UserId, cancellationToken) is { } user ? UserDto.From(user) : IdentityErrors.AccountNotFound;
}

/// <summary>Changes the display name and/or the language; omitted fields are kept.</summary>
public sealed record UpdateProfileCommand(Guid UserId, string? DisplayName, string? Language) : ICommand<UserDto>;

internal sealed class UpdateProfileCommandValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileCommandValidator()
    {
        RuleFor(command => command.DisplayName!).ValidDisplayName().When(command => command.DisplayName is not null);
        RuleFor(command => command.Language).SupportedLanguage();
    }
}

internal sealed class UpdateProfileCommandHandler(IUserRepository users, IIdentityUnitOfWork unitOfWork)
    : ICommandHandler<UpdateProfileCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        if (command.DisplayName is not null)
        {
            user.ChangeDisplayName(command.DisplayName);
        }

        if (command.Language is not null)
        {
            user.ChangeLanguage(LanguageCodes.Parse(command.Language));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return UserDto.From(user);
    }
}

/// <summary>Claims the @username (completes onboarding the first time).</summary>
/// <summary>Changes the username. When creating the account, it also chooses whether the profile is private.</summary>
public sealed record ChangeUsernameCommand(Guid UserId, string Username, bool? IsPrivate = null) : ICommand<UserDto>;

internal sealed class ChangeUsernameCommandValidator : AbstractValidator<ChangeUsernameCommand>
{
    public ChangeUsernameCommandValidator() => RuleFor(command => command.Username).ValidUsernameFormat();
}

internal sealed class ChangeUsernameCommandHandler(IUserRepository users, IIdentityUnitOfWork unitOfWork)
    : ICommandHandler<ChangeUsernameCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(ChangeUsernameCommand command, CancellationToken cancellationToken)
    {
        var username = Username.Create(command.Username);
        if (username.IsFailure)
        {
            return username.Error;
        }

        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        if (user.Username != username.Value && await users.UsernameExistsAsync(username.Value, cancellationToken))
        {
            return IdentityErrors.UsernameTaken;
        }

        user.ChangeUsername(username.Value);
        if (command.IsPrivate is { } isPrivate)
        {
            user.SetPrivate(isPrivate);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return IdentityErrors.UsernameTaken;
        }

        return UserDto.From(user);
    }
}

/// <summary>Makes the profile public or private. Those waiting to follow a profile that becomes public are accepted.</summary>
public sealed record ChangePrivacyCommand(Guid UserId, bool IsPrivate) : ICommand<UserDto>;

internal sealed class ChangePrivacyCommandHandler(IUserRepository users, IIdentityUnitOfWork unitOfWork)
    : ICommandHandler<ChangePrivacyCommand, UserDto>
{
    public async Task<Result<UserDto>> Handle(ChangePrivacyCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        user.SetPrivate(command.IsPrivate);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return UserDto.From(user);
    }
}

public sealed record SettingsDto(string? LiveVisibility, int HideStartDistanceM)
{
    public static SettingsDto From(User user) => new(user.LiveVisibility?.ToString(), user.HideStartDistanceM);
}

public sealed record GetSettingsQuery(Guid UserId) : IQuery<SettingsDto>;

internal sealed class GetSettingsQueryHandler(IUserRepository users) : IQueryHandler<GetSettingsQuery, SettingsDto>
{
    public async Task<Result<SettingsDto>> Handle(GetSettingsQuery query, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(query.UserId, cancellationToken);
        return user is null ? IdentityErrors.AccountNotFound : SettingsDto.From(user);
    }
}

public sealed record UpdateSettingsCommand(Guid UserId, string? LiveVisibility, int? HideStartDistanceM) : ICommand<SettingsDto>;

internal sealed class UpdateSettingsCommandValidator : AbstractValidator<UpdateSettingsCommand>
{
    private static readonly int[] HideStartDistances = [0, 200, 500];

    public UpdateSettingsCommandValidator()
    {
        RuleFor(command => command.LiveVisibility).IsEnumName(typeof(LiveVisibility)).When(command => command.LiveVisibility is not null);
        RuleFor(command => command.HideStartDistanceM)
            .Must(distance => distance is null || HideStartDistances.Contains(distance.Value))
            .WithMessage("It must be 0, 200 or 500.");
    }
}

internal sealed class UpdateSettingsCommandHandler(IUserRepository users, IIdentityUnitOfWork unitOfWork)
    : ICommandHandler<UpdateSettingsCommand, SettingsDto>
{
    public async Task<Result<SettingsDto>> Handle(UpdateSettingsCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.AccountNotFound;
        }

        var visibility = command.LiveVisibility is { } value ? Enum.Parse<LiveVisibility>(value) : (LiveVisibility?)null;
        user.ChangeLiveSettings(visibility, command.HideStartDistanceM);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SettingsDto.From(user);
    }
}

public sealed record CheckUsernameAvailabilityQuery(string Username) : IQuery<UsernameAvailabilityDto>;

internal sealed class CheckUsernameAvailabilityQueryHandler(IUserRepository users)
    : IQueryHandler<CheckUsernameAvailabilityQuery, UsernameAvailabilityDto>
{
    public async Task<Result<UsernameAvailabilityDto>> Handle(CheckUsernameAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var normalized = Username.Normalize(query.Username);
        var reason = Username.Check(normalized) switch
        {
            UsernameCheck.Invalid => UsernameUnavailableReason.Invalid,
            UsernameCheck.Reserved => UsernameUnavailableReason.Reserved,
            _ => await users.UsernameExistsAsync(Username.FromStorage(normalized), cancellationToken)
                ? UsernameUnavailableReason.Taken
                : (UsernameUnavailableReason?)null,
        };

        return new UsernameAvailabilityDto(normalized, reason is null, reason);
    }
}
