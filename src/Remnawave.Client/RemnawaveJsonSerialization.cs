using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;

namespace Remnawave.Client;

/// <summary>
/// JSON (de)serialisation tuned for the Remnawave panel. Uses Kiota's source-generated
/// <see cref="KiotaJsonSerializationContext"/> (no reflection) with one adjustment: dates are
/// written as UTC ISO-8601 with a <c>Z</c> suffix (<c>2025-01-17T15:38:45.065Z</c>), which is the
/// only form accepted by the panel's validators. Kiota's default (<c>+00:00</c>) is rejected.
/// </summary>
public static class RemnawaveJsonSerialization
{
    /// <summary>Serialisation context shared by every client.</summary>
    public static KiotaJsonSerializationContext Context { get; } =
        new(new JsonSerializerOptions { Converters = { new UtcDateTimeOffsetConverter() } });

    /// <summary>Creates a parse-node registry bound to <see cref="Context"/>.</summary>
    public static ParseNodeFactoryRegistry CreateParseNodeFactory()
    {
        var registry = new ParseNodeFactoryRegistry();
        var factory = new JsonParseNodeFactory(Context);
        registry.ContentTypeAssociatedFactories[factory.ValidContentType] = factory;
        return registry;
    }

    /// <summary>Creates a serialisation-writer registry bound to <see cref="Context"/>.</summary>
    public static SerializationWriterFactoryRegistry CreateSerializationWriterFactory()
    {
        var registry = new SerializationWriterFactoryRegistry();
        var factory = new JsonSerializationWriterFactory(Context);
        registry.ContentTypeAssociatedFactories[factory.ValidContentType] = factory;
        return registry;
    }

    /// <summary>Formats a date exactly as the panel expects it (UTC, millisecond precision, <c>Z</c> suffix).</summary>
    public static string FormatDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatDate(value));
    }
}
