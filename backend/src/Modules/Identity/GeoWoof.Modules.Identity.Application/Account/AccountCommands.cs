using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
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
public sealed record ChangeUsernameCommand(Guid UserId, string Username) : ICommand<UserDto>;

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

        if (user.Username == username.Value)
        {
            return UserDto.From(user);
        }

        if (await users.UsernameExistsAsync(username.Value, cancellationToken))
        {
            return IdentityErrors.UsernameTaken;
        }

        user.ChangeUsername(username.Value);
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
