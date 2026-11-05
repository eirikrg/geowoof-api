using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Pets.Domain;

/// <summary>Business errors of the Pets module. Codes are part of the API contract (docs/api.md).</summary>
public static class PetsErrors
{
    public static readonly Error NotFound = Error.NotFound("pets.not_found", "The pet does not exist.");

    /// <summary>The same answer for a person who does not exist and for one with a block in either direction.</summary>
    public static readonly Error UserNotFound = Error.NotFound("identity.user_not_found", "The user does not exist.");

    public static readonly Error NotOwner = Error.Forbidden("pets.not_owner", "Only the owner can do this.");

    public static readonly Error LimitReached =
        Error.Unprocessable("pets.limit_reached", $"A person can have at most {Pet.MaxPerOwner} pets.");

    public static readonly Error WeightNotFound = Error.NotFound("pets.weight_not_found", "The weight entry does not exist.");

    public static readonly Error WeightDateTaken =
        Error.Conflict("pets.weight_date_taken", "There is already a weight entry on that date.");

    public static readonly Error InvalidCursor = Error.Validation("request.invalid", "The cursor is not valid.");
}
