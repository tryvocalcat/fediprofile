namespace FediProfile.Models;

public class UserProgress
{
    public int Id { get; set; } = 1;

    // Total experience earned by the user.
    public int TotalXp { get; set; } = 0;

    // Current calculated level.
    public int Level { get; set; } = 1;

    // Current daily streak.
    public int CurrentStreakDays { get; set; } = 0;

    // Last day the user had meaningful activity.
    // Stored as text because the project is using SQLite-style date strings.
    public string? LastActivityDate { get; set; }

    // Last time this progress row was updated.
    public string UpdatedUtc { get; set; } = string.Empty;
}