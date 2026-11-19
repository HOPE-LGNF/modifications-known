using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModificationsKnown.Core.Updates;

/// <summary>
/// Tolerant storage for <see cref="AppUpdateChannel"/>: missing, null, unknown
/// or non-string values all fall back to Stable so one bad field cannot fail
/// the whole settings file. Written back as "stable" / "preview".
/// </summary>
public sealed class AppUpdateChannelJsonConverter : JsonConverter<AppUpdateChannel>
{
    public override AppUpdateChannel Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            return string.Equals(text, "preview", StringComparison.OrdinalIgnoreCase)
                ? AppUpdateChannel.Preview
                : AppUpdateChannel.Stable;
        }

        reader.Skip();
        return AppUpdateChannel.Stable;
    }

    public override void Write(Utf8JsonWriter writer, AppUpdateChannel value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == AppUpdateChannel.Preview ? "preview" : "stable");
}
