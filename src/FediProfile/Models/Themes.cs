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
    public const string DefaultFile = "theme-default.css";
    public const string BaseFile = "theme-base.css";

    /// <summary>
    /// All available themes, in display order.
    /// Add new themes here — the admin UI and serving logic pick them up automatically.
    /// </summary>
    public static readonly IReadOnlyList<ThemeOption> All = new List<ThemeOption>
    {
        new("theme-default.css",      "Badges"),
        new("theme-classic.css",      "Classic"),
        
        new("theme-cosmos.css",       "Cosmos"),
        
        new("theme-glass.css",        "Glass"),
         new("theme-stars.css",        "Stars"),
        new("theme-midnight.css",     "Midnight"),
        
        new("theme-retropop.css",     "Retro Pop"),
        
        new("theme-hyperspace.css",   "Hyperspace"),
        new("theme-ocean.css",        "Ocean"),
        new("theme-sunset.css",       "Sunset"),
        new("theme-professional.css", "Professional"),
        new("theme-snapgrid.css",     "SnapGrid"),
       
        new("theme-mahodev.css",      "GreenTypo"),
        new("theme-baddie.css",       "Baddie"),
        new("theme-vocalcat.css",     "Metro"),
    };

    /// <summary>
    /// Returns true when <paramref name="fileName"/> matches a known theme.
    /// </summary>
    public static bool IsValid(string? fileName)
        => !string.IsNullOrEmpty(fileName) && All.Any(t => t.FileName == fileName);

    /// <summary>
    /// Returns the subset of known themes that are allowed by the configured list.
    /// An empty or missing configuration falls back to all available themes.
    /// </summary>
    public static IReadOnlyList<string> GetAllowedThemeFiles(IEnumerable<string>? configuredThemeFiles)
    {
        if (configuredThemeFiles == null)
            return All.Select(t => t.FileName).ToList();

        var requestedThemes = configuredThemeFiles
            .Select(t => t?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requestedThemes.Count == 0)
            return All.Select(t => t.FileName).ToList();

        var validThemes = requestedThemes
            .Where(IsValid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return validThemes.Count > 0 ? validThemes : All.Select(t => t.FileName).ToList();
    }

    /// <summary>
    /// Returns the theme options that should be shown to profile owners.
    /// </summary>
    public static IReadOnlyList<ThemeOption> GetAvailableOptions(IEnumerable<string>? configuredThemeFiles)
    {
        var allowedThemeFiles = GetAllowedThemeFiles(configuredThemeFiles);
        return All.Where(t => allowedThemeFiles.Contains(t.FileName, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Returns <paramref name="fileName"/> if it's a known theme and allowed, otherwise the first allowed theme.
    /// </summary>
    public static string ResolveForSelection(string? fileName, IEnumerable<string>? configuredThemeFiles)
    {
        var resolvedTheme = Resolve(fileName);
        var allowedThemeFiles = GetAllowedThemeFiles(configuredThemeFiles);
        return allowedThemeFiles.Contains(resolvedTheme, StringComparer.OrdinalIgnoreCase)
            ? resolvedTheme
            : allowedThemeFiles.FirstOrDefault() ?? DefaultFile;
    }

    /// <summary>
    /// Returns <paramref name="fileName"/> if it's a known theme, otherwise <see cref="DefaultFile"/>.
    /// </summary>
    public static string Resolve(string? fileName)
        => IsValid(fileName) ? fileName! : DefaultFile;
}
