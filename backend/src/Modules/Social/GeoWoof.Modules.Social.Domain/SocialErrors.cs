using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Social.Domain;

/// <summary>Business errors of the Social module. Codes are part of the API contract (docs/api.md).</summary>
public static class SocialErrors
{
    /// <summary>The same answer for a person who does not exist and for one with a block in between.</summary>
    public static readonly Error UserNotFound = Error.NotFound("identity.user_not_found", "The user does not exist.");

    public static readonly Error CannotTargetSelf = Error.Unprocessable("social.cannot_target_self", "This cannot be done with your own account.");

    public static readonly Error RequestNotFound = Error.NotFound("social.request_not_found", "The follow request does not exist.");

    public static readonly Error RequestNotPending = Error.Conflict("social.request_not_pending", "The follow request has already been answered.");

    public static readonly Error ListsPrivate = Error.Forbidden("social.lists_private", "Only this person and their friends can see this list.");

    public static readonly Error InvalidCursor = Error.Validation("request.invalid", "The cursor is not valid.");
}
