namespace FediProfile.Models;

public class UserSettings
{
    public int Id { get; set; }
    public string ActorUsername { get; set; } = "profile";
    public string? ActorBio { get; set; }
    public string? ActorAvatarUrl { get; set; }
    public string UiTheme { get; set; } = "theme-classic.css";
    public string CreatedUtc { get; set; } = string.Empty;
    public string UpdatedUtc { get; set; } = string.Empty;
}
