using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FediProfile.Models;

public class Settings
{
    [Key]
    public int Id { get; set; }
    public string ActorUsername { get; set; } = "profile";
    public string? ActorBio { get; set; }
    public string? ActorAvatarUrl { get; set; }
    public string? InstanceName { get; set; }
    public string? InstanceLogoUrl { get; set; }
    public bool HideFollowersTab { get; set; }
    public bool HideRecentPostsTab { get; set; }
    public bool HideSettingsTab { get; set; }
    public string? LandingMarkdown { get; set; }
    public string UiTheme { get; set; } = "theme-classic.css";
    public string? NavbarLinks { get; set; }
    public string? AdminMastodonUser { get; set; }
    public string? AdminMastodonDomain { get; set; }
    public string? JoinMastodonUrl { get; set; } = "https://joinmastodon.org";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
