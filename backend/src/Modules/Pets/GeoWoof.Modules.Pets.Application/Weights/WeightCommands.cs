using System.Globalization;
using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.Modules.Pets.Application.Abstractions;
using GeoWoof.Modules.Pets.Domain;

namespace GeoWoof.Modules.Pets.Application.Weights;

/// <summary>The weight history is private to the owner: unlike the pet, nobody else can read it.</summary>
public sealed record GetWeightsQuery(Guid UserId, Guid PetId, int? Limit, string? Cursor) : IQuery<PagedResponse<WeightEntryDto>>;

internal sealed class GetWeightsQueryHandler(IPetRepository pets, IPetWeightRepository weights)
    : IQueryHandler<GetWeightsQuery, PagedResponse<WeightEntryDto>>
{
    private const string CursorDateFormat = "yyyy-MM-dd";

    public async Task<Result<PagedResponse<WeightEntryDto>>> Handle(GetWeightsQuery query, CancellationToken cancellationToken)
    {
        var pet = await pets.GetByIdAsync(query.PetId, cancellationToken);
        if (pet is null)
        {
            return PetsErrors.NotFound;
        }

        if (!pet.IsOwnedBy(query.UserId))
        {
            return PetsErrors.NotOwner;
        }

        DateOnly? before = null;
        if (query.Cursor is { } cursor)
        {
            if (!PageCursor.TryDecode(cursor, out var value)
                || !DateOnly.TryParseExact(value, CursorDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return PetsErrors.InvalidCursor;
            }

            before = date;
        }

        var limit = PageLimit.Normalize(query.Limit);
        var page = await weights.GetPageAsync(query.PetId, before, limit + 1, cancellationToken);
        var items = page.Take(limit).Select(WeightEntryDto.From).ToList();

        // One measurement per day: the date alone is a stable position in the list.
        var next = page.Count > limit
            ? PageCursor.Encode(items[^1].MeasuredOn.ToString(CursorDateFormat, CultureInfo.InvariantCulture))
            : null;
        return new PagedResponse<WeightEntryDto>(items, next);
    }
}

public sealed record AddWeightCommand(Guid UserId, Guid PetId, DateOnly MeasuredOn, decimal WeightKg) : ICommand<WeightEntryDto>;

internal sealed class AddWeightCommandValidator : AbstractValidator<AddWeightCommand>
{
    public AddWeightCommandValidator(TimeProvider timeProvider)
    {
        RuleFor(command => command.MeasuredOn)
            .Must(date => PetValidationRules.IsValidPastDate(date, timeProvider))
            .WithMessage("The date cannot be in the future or before 1990.");
        RuleFor(command => command.WeightKg)
            .Must(PetWeightEntry.IsValidWeight)
            .WithMessage($"The weight must be above 0 and up to {PetWeightEntry.MaxWeightKg} kg, with at most 2 decimals.");
    }
}

internal sealed class AddWeightCommandHandler(IPetRepository pets, IPetWeightRepository weights, IPetsUnitOfWork unitOfWork, TimeProvider timeProvider)
    : ICommandHandler<AddWeightCommand, WeightEntryDto>
{
    public async Task<Result<WeightEntryDto>> Handle(AddWeightCommand command, CancellationToken cancellationToken)
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

        var entry = PetWeightEntry.Record(Guid.CreateVersion7(timeProvider.GetUtcNow()), pet.Id, command.MeasuredOn, command.WeightKg);
        weights.Add(entry);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return PetsErrors.WeightDateTaken;
        }

        return WeightEntryDto.From(entry);
    }
}

public sealed record UpdateWeightCommand(Guid UserId, Guid PetId, Guid EntryId, DateOnly MeasuredOn, decimal WeightKg) : ICommand<WeightEntryDto>;

internal sealed class UpdateWeightCommandValidator : AbstractValidator<UpdateWeightCommand>
{
    public UpdateWeightCommandValidator(TimeProvider timeProvider)
    {
        RuleFor(command => command.MeasuredOn)
            .Must(date => PetValidationRules.IsValidPastDate(date, timeProvider))
            .WithMessage("The date cannot be in the future or before 1990.");
        RuleFor(command => command.WeightKg)
            .Must(PetWeightEntry.IsValidWeight)
            .WithMessage($"The weight must be above 0 and up to {PetWeightEntry.MaxWeightKg} kg, with at most 2 decimals.");
    }
}

internal sealed class UpdateWeightCommandHandler(IPetRepository pets, IPetWeightRepository weights, IPetsUnitOfWork unitOfWork)
    : ICommandHandler<UpdateWeightCommand, WeightEntryDto>
{
    public async Task<Result<WeightEntryDto>> Handle(UpdateWeightCommand command, CancellationToken cancellationToken)
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

        var entry = await weights.GetByIdAsync(pet.Id, command.EntryId, cancellationToken);
        if (entry is null)
        {
            return PetsErrors.WeightNotFound;
        }

        entry.Correct(command.MeasuredOn, command.WeightKg);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return PetsErrors.WeightDateTaken;
        }

        return WeightEntryDto.From(entry);
    }
}

public sealed record DeleteWeightCommand(Guid UserId, Guid PetId, Guid EntryId) : ICommand;

internal sealed class DeleteWeightCommandHandler(IPetRepository pets, IPetWeightRepository weights, IPetsUnitOfWork unitOfWork)
    : ICommandHandler<DeleteWeightCommand>
{
    public async Task<Result> Handle(DeleteWeightCommand command, CancellationToken cancellationToken)
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

        var entry = await weights.GetByIdAsync(pet.Id, command.EntryId, cancellationToken);
        if (entry is null)
        {
            return PetsErrors.WeightNotFound;
        }

        weights.Remove(entry);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
