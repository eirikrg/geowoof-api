using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.Modules.Media.Contracts;

/// <summary>An image finished processing: its variants are available.</summary>
[EventType("media.asset_ready.v1")]
public sealed record MediaAssetReady(Guid AssetId, Guid OwnerId, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);

/// <summary>An image was rejected; <paramref name="Reason"/> is a stable <c>media.*</c> code.</summary>
[EventType("media.asset_rejected.v1")]
public sealed record MediaAssetRejected(Guid AssetId, Guid OwnerId, string Reason, DateTimeOffset OccurredAt) : IntegrationEvent(OccurredAt);
