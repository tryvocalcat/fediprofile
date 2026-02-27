using System.Text.Json.Serialization;

namespace FediProfile.Models;

public class FollowActivity
{
    [JsonPropertyName("@context")]
    public string? Context { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; } = "Follow";

    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public class UndoActivity
{
    [JsonPropertyName("@context")]
    public string? Context { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; } = "Undo";

    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("object")]
    public FollowObject? Object { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public class FollowObject
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; } = "Follow";

    [JsonPropertyName("actor")]
    public string? Actor { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }
}
