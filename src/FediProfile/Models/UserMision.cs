namespace FediProfile.Models;

public class UserMission
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int XpReward { get; set; }
    public bool IsCompleted { get; set; }
    public string? CompletedUtc { get; set; }
}