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

    public static readonly Error ProfilePrivate = Error.Forbidden("identity.profile_private", "This profile is private.");

    public static readonly Error PostNotFound = Error.NotFound("social.post_not_found", "The post does not exist.");

    public static readonly Error CommentNotFound = Error.NotFound("social.comment_not_found", "The comment does not exist.");

    public static readonly Error PostIdTaken = Error.Conflict("social.post_id_taken", "That id belongs to another post.");

    public static readonly Error PetNotOwned = Error.Unprocessable("social.pet_not_owned", "Only your own pets can appear in your posts.");

    public static readonly Error NotPostAuthor = Error.Forbidden("social.not_post_author", "Only its author can delete a post.");

    public static readonly Error CannotDeleteComment = Error.Forbidden("social.cannot_delete_comment", "Only its author and the author of the post can delete a comment.");

    public static readonly Error CannotRepost = Error.Forbidden("social.cannot_repost", "You can only repost what friends or public profiles publish.");

    public static readonly Error InvalidCursor = Error.Validation("request.invalid", "The cursor is not valid.");
}
