namespace FediProfile.Models;

/// <summary>
/// Represents a recently boosted/announced post stored for display on the public profile.
/// Only the last N posts are kept per user (rolling window).
/// </summary>
public class RecentPost
{
    public int Id { get; set; }
    /// <summary>The ActivityPub Note ID (URL) — unique per post.</summary>
    public string NoteId { get; set; } = string.Empty;
    /// <summary>The actor URI that authored the original post.</summary>
    public string ActorUri { get; set; } = string.Empty;
    /// <summary>Display name of the original author (if available).</summary>
    public string? ActorName { get; set; }
    /// <summary>Avatar URL of the original author (if available).</summary>
    public string? ActorAvatar { get; set; }
    /// <summary>HTML content of the post.</summary>
    public string? Content { get; set; }
    /// <summary>Plain-text summary (stripped from content) for previews.</summary>
    public string? Summary { get; set; }
    /// <summary>URL to the post on the original instance.</summary>
    public string? Url { get; set; }
    /// <summary>Comma-separated attachment URLs extracted from the Note attachment array.</summary>
    public string? MediaUrls { get; set; }
    /// <summary>Timestamp when the post was originally published.</summary>
    public string? PublishedUtc { get; set; }
    /// <summary>Timestamp when the post was boosted/stored locally.</summary>
    public string BoostedUtc { get; set; } = string.Empty;
}
