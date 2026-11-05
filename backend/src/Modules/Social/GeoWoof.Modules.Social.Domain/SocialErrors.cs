using GeoWoof.BuildingBlocks.Domain.Results;

namespace GeoWoof.Modules.Social.Domain;

/// <summary>Business errors of the Social module. Codes are part of the API contract (docs/api.md).</summary>
public static class SocialErrors
{
    /// <summary>The same answer for a person who does not exist and for one who blocked the caller.</summary>
    public static readonly Error UserNotFound = Error.NotFound("identity.user_not_found", "The user does not exist.");

    public static readonly Error CannotTargetSelf = Error.Unprocessable("social.cannot_target_self", "This cannot be done with your own account.");

    public static readonly Error AlreadyFriends = Error.Conflict("social.already_friends", "You are already friends.");

    public static readonly Error RequestNotFound = Error.NotFound("social.request_not_found", "The friend request does not exist.");

    public static readonly Error RequestNotPending = Error.Conflict("social.request_not_pending", "The friend request has already been answered.");

    public static readonly Error NotFriends = Error.NotFound("social.not_friends", "You are not friends.");

    public static readonly Error InvalidCursor = Error.Validation("request.invalid", "The cursor is not valid.");
}
