using System.Globalization;
using GeoWoof.BuildingBlocks.Application.Paging;
using GeoWoof.Modules.Identity.Contracts;
using GeoWoof.Modules.Media.Contracts;
using GeoWoof.Modules.Social.Application.Abstractions;

namespace GeoWoof.Modules.Social.Application;

public sealed record UserSummaryDto(Guid Id, string Username, string DisplayName, string? City, MediaImage? Avatar);

public sealed record FriendRequestDto(Guid Id, UserSummaryDto User, string Direction, string Status, DateTimeOffset CreatedAt);

public sealed record FriendDto(UserSummaryDto User, DateTimeOffset Since);

public sealed record BlockDto(UserSummaryDto User, DateTimeOffset Since);

public sealed record RelationshipDto(string Status, Guid? RequestId);

public sealed record ReportDto(Guid Id);

/// <summary>Builds the people of a list, with the signed URLs of their avatars.</summary>
internal sealed class UserSummaries(IIdentityApi identity, IMediaApi media)
{
    /// <summary>An account being deleted, whose event has not arrived yet, is left out.</summary>
    public async Task<IReadOnlyDictionary<Guid, UserSummaryDto>> LoadAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var users = await identity.GetPublicUsersAsync(userIds, cancellationToken);
        var assetIds = users.Values.Where(user => user.AvatarAssetId is not null).Select(user => user.AvatarAssetId!.Value).ToList();
        var images = await media.GetImagesAsync(assetIds, cancellationToken);
        return users.Values.ToDictionary(
            user => user.Id,
            user => new UserSummaryDto(
                user.Id,
                user.Username,
                user.DisplayName,
                user.City,
                user.AvatarAssetId is { } assetId ? images.GetValueOrDefault(assetId) : null));
    }

    public async Task<UserSummaryDto?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        (await LoadAsync([userId], cancellationToken)).GetValueOrDefault(userId);
}

internal static class SocialCursor
{
    private const char Separator = '|';

    public static string Encode(DateTimeOffset at, Guid id) =>
        PageCursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks}{Separator}{id}"));

    public static SocialPosition? Decode(string cursor)
    {
        if (!PageCursor.TryDecode(cursor, out var value))
        {
            return null;
        }

        var parts = value.Split(Separator);
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && Guid.TryParse(parts[1], out var id)
                ? new SocialPosition(new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
    }
}
