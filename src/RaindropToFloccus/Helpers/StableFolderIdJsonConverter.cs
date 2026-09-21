namespace RaindropToFloccus.Helpers;

public sealed class StableFolderIdJsonConverter : JsonConverter<StableFolderId>
{
    public override StableFolderId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !Guid.TryParseExact(reader.GetString(), "D", out var value)
            || value == Guid.Empty)
        {
            throw new JsonException("A stable folder ID must be a non-empty UUID.");
        }

        return new StableFolderId(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        StableFolderId value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value.ToString("D"));
    }
}
