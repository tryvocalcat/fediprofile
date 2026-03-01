namespace FediProfile.Models;

public class Follower
{
    public string FollowerUri { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string? AvatarUri { get; set; }
    public string? DisplayName { get; set; }
    public string Inbox { get; set; } = string.Empty;
    public int Status { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
}
