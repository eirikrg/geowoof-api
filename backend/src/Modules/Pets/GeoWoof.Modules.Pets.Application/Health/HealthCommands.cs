using System.Globalization;
using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Pets.Application.Abstractions;
using GeoWoof.Modules.Pets.Domain;

namespace GeoWoof.Modules.Pets.Application.Health;

public sealed record HealthRepeatDto(int Every, string Unit, DateOnly? Until);

/// <summary>A health event as the API shows it. The time is local and written HH:mm.</summary>
public sealed record HealthEventDto(
    Guid Id,
    Guid PetId,
    string Type,
    string Title,
    DateOnly DueOn,
    string? DueAt,
    DateOnly? DoneOn,
    HealthRepeatDto? Repeat,
    string Reminder,
    string? Notes,
    DateTimeOffset UpdatedAt)
{
    internal const string TimeFormat = "HH:mm";

    internal static HealthEventDto From(HealthEvent healthEvent) => new(
        healthEvent.Id,
        healthEvent.PetId,
        healthEvent.Type.ToString(),
        healthEvent.Title,
        healthEvent.DueOn,
        healthEvent.DueAt?.ToString(TimeFormat, CultureInfo.InvariantCulture),
        healthEvent.DoneOn,
        healthEvent.Repeat is { } repeat ? new HealthRepeatDto(repeat.Every, repeat.Unit.ToString(), repeat.Until) : null,
        healthEvent.Reminder.ToString(),
        healthEvent.Notes,
        healthEvent.UpdatedAt);
}

/// <summary>What was done and, if it repeats, the next one, which the server makes.</summary>
public sealed record HealthCompletionDto(HealthEventDto Done, HealthEventDto? Next);

/// <summary>The fields of a health event as they arrive from the API: the enums and the time are validated strings.</summary>
public sealed record HealthEventInput(
    string Type,
    string? Title,
    DateOnly DueOn,
    string? DueAt,
    DateOnly? DoneOn,
    HealthRepeatDto? Repeat,
    string Reminder,
    string? Notes)
{
    internal HealthEventDetails ToDetails() => new(
        Enum.Parse<HealthEventType>(Type),
        Title!,
        DueOn,
        DueAt is null ? null : TimeOnly.ParseExact(DueAt, HealthEventDto.TimeFormat, CultureInfo.InvariantCulture),
        DoneOn,
        Repeat is null ? null : new HealthRepeat(Repeat.Every, Enum.Parse<RepeatUnit>(Repeat.Unit), Repeat.Until),
        Enum.Parse<HealthReminder>(Reminder),
        Notes);
}

internal sealed class HealthEventInputValidator : AbstractValidator<HealthEventInput>
{
    public HealthEventInputValidator(TimeProvider timeProvider)
    {
        RuleFor(input => input.Type).ValidEnum<HealthEventInput, HealthEventType>();
        RuleFor(input => input.Title)
            .Must(title => !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= HealthEvent.TitleMaxLength)
            .WithMessage($"The title must have 1 to {HealthEvent.TitleMaxLength} characters.");
        RuleFor(input => input.DueOn)
            .Must(date => date >= Pet.EarliestDate && date <= Today(timeProvider).AddYears(HealthEvent.MaxYearsAhead))
            .WithMessage($"The date cannot be before 1990 or more than {HealthEvent.MaxYearsAhead} years ahead.");
        RuleFor(input => input.DueAt)
            .Must(time => TimeOnly.TryParseExact(time, HealthEventDto.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .When(input => input.DueAt is not null)
            .WithMessage("The time is written HH:mm.");
        RuleFor(input => input.DoneOn)
            .Must(date => PetValidationRules.IsValidPastDate(date!.Value, timeProvider))
            .When(input => input.DoneOn is not null)
            .WithMessage("The date when it was done cannot be in the future or before 1990.");
        When(input => input.Repeat is not null, () =>
        {
            RuleFor(input => input.Repeat!.Every)
                .InclusiveBetween(1, HealthRepeat.MaxEvery)
                .OverridePropertyName("repeat.every");
            RuleFor(input => input.Repeat!.Unit)
                .ValidEnum<HealthEventInput, RepeatUnit>()
                .OverridePropertyName("repeat.unit");
            RuleFor(input => input.Repeat!.Until)
                .Must((input, until) => until is null || until >= input.DueOn)
                .WithMessage("The repetition cannot end before the first time.")
                .OverridePropertyName("repeat.until");
        });
        RuleFor(input => input.Reminder).ValidEnum<HealthEventInput, HealthReminder>();
        RuleFor(input => input.Notes).MaximumLength(HealthEvent.NotesMaxLength);
    }

    /// <summary>A day ahead of UTC, as the dates of the owner can be.</summary>
    private static DateOnly Today(TimeProvider timeProvider) => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(1);
}

/// <summary>All the health of a pet, which only its owner sees: there are few, so it is not paged.</summary>
public sealed record GetHealthQuery(Guid UserId, Guid PetId) : IQuery<PagedResponse<HealthEventDto>>;

internal sealed class GetHealthQueryHandler(IPetRepository pets, IHealthEventRepository events)
    : IQueryHandler<GetHealthQuery, PagedResponse<HealthEventDto>>
{
    public async Task<Result<PagedResponse<HealthEventDto>>> Handle(GetHealthQuery query, CancellationToken cancellationToken)
    {
        if (await HealthAccess.CheckAsync(pets, query.UserId, query.PetId, cancellationToken) is { } problem)
        {
            return problem;
        }

        var items = await events.GetByPetAsync(query.PetId, cancellationToken);
        return new PagedResponse<HealthEventDto>(items.Select(HealthEventDto.From).ToList(), null);
    }
}

public sealed record PutHealthEventResult(HealthEventDto Event, bool Created);

/// <summary>
/// Creates the event with the identifier that the app gives it, or replaces it whole: repeating the request does not
/// create another one.
/// </summary>
public sealed record PutHealthEventCommand(Guid UserId, Guid PetId, Guid EventId, HealthEventInput Input) : ICommand<PutHealthEventResult>;

internal sealed class PutHealthEventCommandValidator : AbstractValidator<PutHealthEventCommand>
{
    public PutHealthEventCommandValidator(TimeProvider timeProvider) =>
        RuleFor(command => command.Input)
            .SetValidator(new HealthEventInputValidator(timeProvider))
            .OverridePropertyName(string.Empty);
}

internal sealed class PutHealthEventCommandHandler(
    IPetRepository pets,
    IHealthEventRepository events,
    IPetsUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<PutHealthEventCommand, PutHealthEventResult>
{
    public async Task<Result<PutHealthEventResult>> Handle(PutHealthEventCommand command, CancellationToken cancellationToken)
    {
        if (await HealthAccess.CheckAsync(pets, command.UserId, command.PetId, cancellationToken) is { } problem)
        {
            return problem;
        }

        var now = timeProvider.GetUtcNow();
        var details = command.Input.ToDetails();
        var existing = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (existing is not null)
        {
            if (existing.PetId != command.PetId)
            {
                return PetsErrors.HealthEventIdTaken;
            }

            existing.Replace(details, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new PutHealthEventResult(HealthEventDto.From(existing), Created: false);
        }

        if (await events.CountByPetAsync(command.PetId, cancellationToken) >= HealthEvent.MaxPerPet)
        {
            return PetsErrors.HealthLimitReached;
        }

        var created = HealthEvent.Create(command.EventId, command.PetId, details, now);
        events.Add(created);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // The same identifier arrived twice at once, or it belongs to another pet.
            return PetsErrors.HealthEventIdTaken;
        }

        return new PutHealthEventResult(HealthEventDto.From(created), Created: true);
    }
}

public sealed record CompleteHealthEventCommand(Guid UserId, Guid PetId, Guid EventId, DateOnly DoneOn) : ICommand<HealthCompletionDto>;

internal sealed class CompleteHealthEventCommandValidator : AbstractValidator<CompleteHealthEventCommand>
{
    public CompleteHealthEventCommandValidator(TimeProvider timeProvider) =>
        RuleFor(command => command.DoneOn)
            .Must(date => PetValidationRules.IsValidPastDate(date, timeProvider))
            .WithMessage("The date when it was done cannot be in the future or before 1990.");
}

internal sealed class CompleteHealthEventCommandHandler(
    IPetRepository pets,
    IHealthEventRepository events,
    IPetsUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<CompleteHealthEventCommand, HealthCompletionDto>
{
    public async Task<Result<HealthCompletionDto>> Handle(CompleteHealthEventCommand command, CancellationToken cancellationToken)
    {
        if (await HealthAccess.CheckAsync(pets, command.UserId, command.PetId, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (await events.GetByIdAsync(command.EventId, cancellationToken) is not { } healthEvent || healthEvent.PetId != command.PetId)
        {
            return PetsErrors.HealthEventNotFound;
        }

        var now = timeProvider.GetUtcNow();
        var next = healthEvent.Complete(command.DoneOn, Guid.CreateVersion7(now), now);
        if (next is not null)
        {
            events.Add(next);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new HealthCompletionDto(HealthEventDto.From(healthEvent), next is null ? null : HealthEventDto.From(next));
    }
}

public sealed record DeleteHealthEventCommand(Guid UserId, Guid PetId, Guid EventId) : ICommand;

internal sealed class DeleteHealthEventCommandHandler(IPetRepository pets, IHealthEventRepository events, IPetsUnitOfWork unitOfWork)
    : ICommandHandler<DeleteHealthEventCommand>
{
    public async Task<Result> Handle(DeleteHealthEventCommand command, CancellationToken cancellationToken)
    {
        if (await HealthAccess.CheckAsync(pets, command.UserId, command.PetId, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (await events.GetByIdAsync(command.EventId, cancellationToken) is { } healthEvent && healthEvent.PetId == command.PetId)
        {
            events.Remove(healthEvent);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}

internal static class HealthAccess
{
    /// <summary>The pet exists and is of this person: like the weights, nobody else sees its health.</summary>
    public static async Task<Error?> CheckAsync(IPetRepository pets, Guid userId, Guid petId, CancellationToken cancellationToken)
    {
        var pet = await pets.GetByIdAsync(petId, cancellationToken);
        if (pet is null)
        {
            return PetsErrors.NotFound;
        }

        return pet.IsOwnedBy(userId) ? null : PetsErrors.NotOwner;
    }
}
