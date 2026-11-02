using System.Text.Json;
using System.Text.Json.Serialization;
using GeoWoof.BuildingBlocks.Contracts;

namespace GeoWoof.BuildingBlocks.Infrastructure.Events;

/// <summary>JSON format of integration events in the Outbox (camelCase, enums as strings).</summary>
internal static class IntegrationEventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(IIntegrationEvent integrationEvent) =>
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), Options);

    public static IIntegrationEvent Deserialize(string payload, Type eventType) =>
        JsonSerializer.Deserialize(payload, eventType, Options) as IIntegrationEvent
        ?? throw new JsonException($"The payload is not a valid '{eventType.FullName}'.");
}
