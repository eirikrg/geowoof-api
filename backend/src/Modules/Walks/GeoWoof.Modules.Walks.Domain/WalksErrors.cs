using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Walks.Domain;

/// <summary>Business errors of the Walks module. Codes are part of the API contract.</summary>
public static class WalksErrors
{
    public static readonly Error NotFound = Error.NotFound("walks.not_found", "The walk does not exist.");

    public static readonly Error RouteImmutable =
        Error.Conflict("walks.route_immutable", "The route, dates and pets of a saved walk cannot change.");

    public static readonly Error PetNotOwned =
        Error.Unprocessable("walks.pet_not_owned", "Every pet of a walk must belong to its owner.");

    public static readonly Error InvalidTimeRange =
        Error.Unprocessable("walks.invalid_time_range", "A walk must end after it starts, and it cannot be in the future.");

    public static readonly Error TooLong =
        Error.Unprocessable("walks.too_long", $"A walk can last at most {Walk.MaxDuration.TotalHours} hours.");

    public static readonly Error InvalidTrack = Error.Unprocessable("walks.invalid_track", "The route of the walk is not valid.");

    public static readonly Error TooManyPoints =
        Error.Unprocessable("walks.too_many_points", $"A walk can have at most {WalkTrack.MaxPoints} points.");

    public static readonly Error PetNotFound = Error.NotFound("pets.not_found", "The pet does not exist.");

    public static readonly Error NotPetOwner = Error.Forbidden("pets.not_owner", "Only the owner can do this.");

    /// <summary>The same answer for a person who does not exist and for one with a block in either direction.</summary>
    public static readonly Error UserNotFound = Error.NotFound("identity.user_not_found", "The user does not exist.");

    public static readonly Error ProfilePrivate = Error.Forbidden("identity.profile_private", "This profile is private.");

    public static readonly Error InvalidCursor = Error.Validation("request.invalid", "The cursor is not valid.");
}
