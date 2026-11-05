using FluentValidation;
using GeoWoof.BuildingBlocks.Application.Messaging;
using GeoWoof.BuildingBlocks.Application.Persistence;
using GeoWoof.BuildingBlocks.Domain.Results;
using GeoWoof.BuildingBlocks.Domain.Visibility;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Pets.Contracts;
using GeoWoof.Modules.Walks.Application.Abstractions;
using GeoWoof.Modules.Walks.Domain;
using Microsoft.Extensions.Logging;

namespace GeoWoof.Modules.Walks.Application.Walks;

public sealed record WalkClientStats(int DistanceM, int MovingTimeS);

/// <summary>A walk as the phone sends it. Enums are validated strings, and its numbers are only for diagnostics.</summary>
public sealed record WalkInput(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string TimeZone,
    IReadOnlyList<Guid> PetIds,
    string Visibility,
    string? Note,
    IReadOnlyList<Guid>? PhotoAssetIds,
    IReadOnlyList<WalkSegmentDto> Segments,
    WalkClientStats? ClientStats,
    DateTimeOffset UpdatedAt);

/// <summary>Creates a walk, or updates what is editable of an existing one. Repeating it never duplicates anything.</summary>
public sealed record PutWalkCommand(Guid OwnerId, Guid WalkId, WalkInput Input) : ICommand<PutWalkResult>;

internal sealed class PutWalkCommandValidator : AbstractValidator<PutWalkCommand>
{
    public PutWalkCommandValidator() =>
        RuleFor(command => command.Input)
            .SetValidator(new WalkInputValidator())

            // The API reports the fields of the request (`note`), not of this command (`input.note`).
            .OverridePropertyName(string.Empty);
}

internal sealed class WalkInputValidator : AbstractValidator<WalkInput>
{
    public WalkInputValidator()
    {
        RuleFor(input => input.TimeZone)
            .Must(zone => zone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
            .WithMessage("Use a valid IANA time zone, such as Europe/Madrid.");
        RuleFor(input => input.Visibility)
            .Must(visibility => Enum.GetNames<ContentVisibility>().Contains(visibility, StringComparer.Ordinal))
            .WithMessage($"Use one of: {string.Join(", ", Enum.GetNames<ContentVisibility>())}.");
        RuleFor(input => input.Note).MaximumLength(Walk.NoteMaxLength);
        RuleFor(input => input.PetIds)
            .Must(pets => pets is { Count: > 0 and <= Walk.MaxPets } && pets.Distinct().Count() == pets.Count)
            .WithMessage($"A walk has between 1 and {Walk.MaxPets} different pets.");
        RuleFor(input => input.PhotoAssetIds)
            .Must(photos => photos is null || (photos.Count <= Walk.MaxPhotos && photos.Distinct().Count() == photos.Count))
            .WithMessage($"A walk has at most {Walk.MaxPhotos} different photos.");
        RuleFor(input => input.Segments).NotNull();
        RuleFor(input => input.UpdatedAt).NotEmpty();
    }
}

internal sealed partial class PutWalkCommandHandler(
    IWalkRepository walks,
    IPetsApi pets,
    IMediaApi media,
    IWalksUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<PutWalkCommandHandler> logger) : ICommandHandler<PutWalkCommand, PutWalkResult>
{
    /// <summary>A bigger difference between phone and server deserves a look at the filters of the app.</summary>
    private const double SuspiciousDifference = 0.2;

    public async Task<Result<PutWalkResult>> Handle(PutWalkCommand command, CancellationToken cancellationToken)
    {
        var now = ToStoredPrecision(timeProvider.GetUtcNow());
        var input = command.Input with
        {
            StartedAt = ToStoredPrecision(command.Input.StartedAt),
            EndedAt = ToStoredPrecision(command.Input.EndedAt),

            // A phone with its clock ahead must not make its edit win over every later one.
            UpdatedAt = Earliest(ToStoredPrecision(command.Input.UpdatedAt), now),
        };

        var existing = await walks.GetByIdAsync(command.WalkId, cancellationToken);
        if (existing is not null)
        {
            return await UpdateAsync(existing, command.OwnerId, input, cancellationToken);
        }

        try
        {
            return await CreateAsync(command.WalkId, command.OwnerId, input, now, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // The same upload arrived twice at the same moment: the other request created the walk, so this one is a retry.
            unitOfWork.DiscardChanges();
            return await Handle(command, cancellationToken);
        }
    }

    private async Task<Result<PutWalkResult>> CreateAsync(Guid walkId, Guid ownerId, WalkInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var duration = input.EndedAt - input.StartedAt;
        if (duration <= TimeSpan.Zero || input.EndedAt > now + Walk.ClockTolerance)
        {
            return WalksErrors.InvalidTimeRange;
        }

        if (duration > Walk.MaxDuration)
        {
            return WalksErrors.TooLong;
        }

        var track = WalkTrack.Parse(ToSegments(input), duration);
        if (track.IsFailure)
        {
            return track.Error;
        }

        var ownedPets = await pets.GetPetIdsAsync(ownerId, cancellationToken);
        if (!input.PetIds.All(ownedPets.Contains))
        {
            return WalksErrors.PetNotOwned;
        }

        var photos = input.PhotoAssetIds ?? [];
        var attached = await AttachAsync(photos, ownerId, cancellationToken);
        if (attached.IsFailure)
        {
            return attached.Error;
        }

        var details = new WalkDetails(Enum.Parse<ContentVisibility>(input.Visibility), input.Note, photos);
        var walk = Walk.Record(walkId, ownerId, input.StartedAt, input.EndedAt, input.TimeZone, input.PetIds, details, track.Value, input.UpdatedAt, now);
        LogIfSuspicious(walk, input.ClientStats);
        walks.Add(walk, track.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new PutWalkResult(await WalkMapper.ToDtoAsync(walk, ownerId, media, cancellationToken), Created: true);
    }

    private async Task<Result<PutWalkResult>> UpdateAsync(Walk walk, Guid ownerId, WalkInput input, CancellationToken cancellationToken)
    {
        if (walk.OwnerId != ownerId)
        {
            return WalksErrors.NotFound;
        }

        if (!await IsSameWalkAsync(walk, input, cancellationToken))
        {
            return WalksErrors.RouteImmutable;
        }

        if (walk.IsStale(input.UpdatedAt))
        {
            return new PutWalkResult(await WalkMapper.ToDtoAsync(walk, ownerId, media, cancellationToken), Created: false);
        }

        var photos = input.PhotoAssetIds ?? [];
        var previous = walk.PhotoAssetIds;
        var attached = await AttachAsync(photos.Except(previous).ToList(), ownerId, cancellationToken);
        if (attached.IsFailure)
        {
            return attached.Error;
        }

        walk.Update(new WalkDetails(Enum.Parse<ContentVisibility>(input.Visibility), input.Note, photos), input.UpdatedAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var removed in previous.Except(photos))
        {
            await media.ReleaseAsync(removed, ownerId, cancellationToken);
        }

        return new PutWalkResult(await WalkMapper.ToDtoAsync(walk, ownerId, media, cancellationToken), Created: false);
    }

    private async Task<bool> IsSameWalkAsync(Walk walk, WalkInput input, CancellationToken cancellationToken)
    {
        if (!walk.IsSameRecording(input.StartedAt, input.EndedAt, ToSegments(input), input.PetIds))
        {
            return false;
        }

        // The phone may still send a pet deleted after the walk was saved. Any other new pet is a real change.
        var added = input.PetIds.Except(walk.Pets.Select(pet => pet.PetId)).ToList();
        return added.Count == 0 || !(await pets.GetPetIdsAsync(walk.OwnerId, cancellationToken)).Intersect(added).Any();
    }

    private async Task<Result> AttachAsync(IReadOnlyList<Guid> photos, Guid ownerId, CancellationToken cancellationToken)
    {
        foreach (var photo in photos)
        {
            var failure = await media.AttachAsync(photo, ownerId, MediaUsage.WalkPhoto, cancellationToken);
            if (failure is not null)
            {
                return failure.IsNotFound ? Error.NotFound(failure.Code, failure.Message) : Error.Unprocessable(failure.Code, failure.Message);
            }
        }

        return Result.Success();
    }

    private static DateTimeOffset Earliest(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;

    /// <summary>UTC and milliseconds, the precision of the time offsets: a retry always compares equal to what was stored.</summary>
    private static DateTimeOffset ToStoredPrecision(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

    private static List<WalkSegment> ToSegments(WalkInput input) =>
        input.Segments.Select(segment => new WalkSegment(segment.Polyline, segment.TimeOffsetsMs)).ToList();

    private void LogIfSuspicious(Walk walk, WalkClientStats? client)
    {
        if (client is not null && walk.DistanceM > 0 && Math.Abs(client.DistanceM - walk.DistanceM) / (double)walk.DistanceM > SuspiciousDifference)
        {
            LogDistanceDifference(logger, walk.Id, client.DistanceM, walk.DistanceM);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Walk {WalkId}: the phone calculated {ClientDistanceM} m and the server {ServerDistanceM} m")]
    private static partial void LogDistanceDifference(ILogger logger, Guid walkId, int clientDistanceM, int serverDistanceM);
}
