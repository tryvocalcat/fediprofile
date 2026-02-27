using System.Text.Json.Serialization;

namespace FediProfile.Models;

public class ActivityPubCollection
{
    [JsonPropertyName("@context")]
    public string Context { get; set; } = "https://www.w3.org/ns/activitystreams";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Collection";

    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    [JsonPropertyName("orderedItems")]
    public object OrderedItems { get; set; } = Array.Empty<string>();
}
