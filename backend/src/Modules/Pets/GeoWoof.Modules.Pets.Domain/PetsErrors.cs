using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Pets.Domain;

/// <summary>Business errors of the Pets module. Codes are part of the API contract.</summary>
public static class PetsErrors
{
    public static readonly Error NotFound = Error.NotFound("pets.not_found", "The pet does not exist.");

    /// <summary>The same answer for a person who does not exist and for one with a block in either direction.</summary>
    public static readonly Error UserNotFound = Error.NotFound("identity.user_not_found", "The user does not exist.");

    public static readonly Error ProfilePrivate = Error.Forbidden("identity.profile_private", "This profile is private.");

    public static readonly Error NotOwner = Error.Forbidden("pets.not_owner", "Only the owner can do this.");

    public static readonly Error LimitReached =
        Error.Unprocessable("pets.limit_reached", $"A person can have at most {Pet.MaxPerOwner} pets.");

    public static readonly Error WeightNotFound = Error.NotFound("pets.weight_not_found", "The weight entry does not exist.");

    public static readonly Error WeightDateTaken =
        Error.Conflict("pets.weight_date_taken", "There is already a weight entry on that date.");

    public static readonly Error HealthEventNotFound =
        Error.NotFound("pets.health_event_not_found", "The health event does not exist.");

    public static readonly Error HealthEventIdTaken =
        Error.Conflict("pets.health_event_id_taken", "That identifier belongs to a health event of another pet.");

    public static readonly Error HealthLimitReached =
        Error.Unprocessable("pets.health_limit_reached", $"A pet can have at most {HealthEvent.MaxPerPet} health events.");

    public static readonly Error InvalidCursor = Error.Validation("request.invalid", "The cursor is not valid.");
}
