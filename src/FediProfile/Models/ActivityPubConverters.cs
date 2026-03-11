using System.Text.Json;
using System.Text.Json.Serialization;

namespace FediProfile.Models;

/// <summary>
/// Handles icon/image fields that can be a single ActivityPubImage or an array.
/// When an array, picks the largest image by pixel area.
/// </summary>
public class SingleOrBestImageConverter : JsonConverter<ActivityPubImage?>
{
    public override ActivityPubImage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var images = JsonSerializer.Deserialize<List<ActivityPubImage>>(ref reader, options);
            return PickBest(images);
        }

        return JsonSerializer.Deserialize<ActivityPubImage>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, ActivityPubImage? value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }

    internal static ActivityPubImage? PickBest(List<ActivityPubImage>? images)
        => images?.OrderByDescending(i => (i.Width ?? 0) * (i.Height ?? 0)).FirstOrDefault();
}

/// <summary>
/// Handles url fields that can be a single string or an array of link objects.
/// When an array, picks the first text/html link's href.
/// </summary>
public class SingleOrFirstUrlConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var links = JsonSerializer.Deserialize<List<ActivityPubUrlLink>>(ref reader, options);
            return PickBest(links);
        }

        return reader.GetString();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }

    internal static string? PickBest(List<ActivityPubUrlLink>? links)
        => links?.FirstOrDefault(l => l.MediaType == "text/html")?.Href
           ?? links?.FirstOrDefault()?.Href;
}

/// <summary>
/// Represents a link object in an ActivityPub url array (e.g. PeerTube).
/// </summary>
public class ActivityPubUrlLink
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("href")]
    public string? Href { get; set; }
}
