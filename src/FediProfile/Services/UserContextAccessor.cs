namespace FediProfile.Services;

/// <summary>
/// UserContextAccessor provides access to the current user slug from the HTTP context.
/// The user slug is extracted from the first segment of the URL path (e.g., /maho/profile → maho).
/// </summary>
public class UserContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UserContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Gets the user slug from the current request path.
    /// Returns null if no user slug is found or if the path is a special route (.well-known, /admin, etc).
    /// </summary>
    public string? GetUserSlug()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
            return null;

        var path = httpContext.Request.Path.Value?.Trim('/');
        if (string.IsNullOrEmpty(path))
            return null;

        // Don't extract user slug from special routes
        if (path.StartsWith(".well-known") || 
            path.StartsWith("signin") ||
            path.Equals("", StringComparison.OrdinalIgnoreCase))
            return null;

        // Extract the first segment of the path
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var potentialSlug = segments[0];

        // Validate slug format (alphanumeric, hyphens, underscores)
        if (!IsValidSlug(potentialSlug))
            return null;

        // Don't return reserved slugs as user slugs
        if (LocalDbService.IsReservedUserSlug(potentialSlug))
            return null;

        return potentialSlug;
    }

    /// <summary>
    /// Checks if a slug is valid (contains only alphanumeric characters, hyphens, and underscores).
    /// </summary>
    private bool IsValidSlug(string slug)
    {
        return !string.IsNullOrEmpty(slug) && 
               slug.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }
}
