namespace FediProfile.Models;

/// <summary>
/// Represents a selectable UI theme for profile pages.
/// </summary>
public record ThemeOption(string FileName, string DisplayName);

/// <summary>
/// Central registry of all available themes.
/// Every reference to a theme file name or the default theme should go through this class.
/// </summary>
public static class Themes
{
    public const string DefaultFile = "theme-classic.css";
    public const string BaseFile = "theme-base.css";

    /// <summary>
    /// All available themes, in display order.
    /// Add new themes here — the admin UI and serving logic pick them up automatically.
    /// </summary>
    public static readonly IReadOnlyList<ThemeOption> All = new List<ThemeOption>
    {
        new("theme-classic.css",  "Classic"),
        new("theme-midnight.css", "Midnight"),
        new("theme-ocean.css",    "Ocean"),
        new("theme-sunset.css",   "Sunset"),
    };

    /// <summary>
    /// Returns true when <paramref name="fileName"/> matches a known theme.
    /// </summary>
    public static bool IsValid(string? fileName)
        => !string.IsNullOrEmpty(fileName) && All.Any(t => t.FileName == fileName);

    /// <summary>
    /// Returns <paramref name="fileName"/> if it's a known theme, otherwise <see cref="DefaultFile"/>.
    /// </summary>
    public static string Resolve(string? fileName)
        => IsValid(fileName) ? fileName! : DefaultFile;
}
