using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoWoof.Api.Extensions;

/// <summary>
/// Writes dates in UTC with a final <c>Z</c>, as docs/api.md says. The default converter writes <c>+00:00</c>,
/// which <c>java.time.Instant.parse</c> rejects on Android 13 and older.
/// </summary>
internal sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime);
}
