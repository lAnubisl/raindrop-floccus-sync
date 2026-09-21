namespace RaindropToFloccus.Helpers;

public sealed class StableBookmarkIdJsonConverter : JsonConverter<StableBookmarkId>
{
    public override StableBookmarkId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !Guid.TryParseExact(reader.GetString(), "D", out var value)
            || value == Guid.Empty)
        {
            throw new JsonException("A stable bookmark ID must be a non-empty UUID.");
        }

        return new StableBookmarkId(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        StableBookmarkId value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value.ToString("D"));
    }
}
