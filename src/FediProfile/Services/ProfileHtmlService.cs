using System.Net;
using System.Text;

namespace FediProfile.Services;

/// <summary>
/// Generates a static profile HTML file for each user by injecting rel="me" links,
/// OpenGraph meta tags, and theme into the base profile.html template.
/// 
/// Pre-generated files are stored at wwwroot/profiles/{userSlug}.html.
/// This avoids per-request DB queries: the file is regenerated only when the user
/// saves their profile or changes their links.
/// </summary>
public class ProfileHtmlService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ProfileHtmlService> _logger;

    public ProfileHtmlService(IWebHostEnvironment env, ILogger<ProfileHtmlService> logger)
    {
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// (Re)generates the static profile HTML for the given user.
    /// Reads data from the user's database and injects it into profile.html.
    /// </summary>
    public async Task GenerateAsync(UserScopedDb userDb, string userSlug, string baseDomain)
    {
        try
        {
            var templatePath = Path.Combine(_env.WebRootPath, "profile.html");
            if (!File.Exists(templatePath))
            {
                _logger.LogWarning("profile.html template not found at {Path}", templatePath);
                return;
            }

            var html = await File.ReadAllTextAsync(templatePath);

            // Fetch profile data
            var profileName = await userDb.GetActorUsernameAsync() ?? userSlug;
            var profileBio = await userDb.GetActorBioAsync() ?? "";
            var profileAvatar = await userDb.GetActorAvatarUrlAsync();
            var theme = await userDb.GetUiThemeAsync();
            var links = await userDb.GetLinksAsync(false); // non-hidden links only

            var head = new StringBuilder();

            // rel="me" link tags for Mastodon verification (crawlers don't execute JS)
            foreach (var link in links)
            {
                var url = link.Url;
                if (!string.IsNullOrEmpty(url))
                {
                    head.Append($"    <link rel=\"me\" href=\"{WebUtility.HtmlEncode(url)}\" />\n");
                }
            }

            // OpenGraph meta tags for richer social previews
            head.Append($"    <meta property=\"og:title\" content=\"{WebUtility.HtmlEncode(profileName)}\" />\n");
            if (!string.IsNullOrEmpty(profileBio))
                head.Append($"    <meta property=\"og:description\" content=\"{WebUtility.HtmlEncode(profileBio)}\" />\n");
            if (!string.IsNullOrEmpty(profileAvatar))
            {
                var fullAvatar = profileAvatar.StartsWith("/") ? $"{baseDomain}{profileAvatar}" : profileAvatar;
                head.Append($"    <meta property=\"og:image\" content=\"{WebUtility.HtmlEncode(fullAvatar)}\" />\n");
            }
            head.Append($"    <meta property=\"og:type\" content=\"profile\" />\n");

            // Update the page title to include the user's name
            html = html.Replace("<title>FediProfile</title>", $"<title>{WebUtility.HtmlEncode(profileName)} - FediProfile</title>");

            // Inject theme link if not the default
            if (!string.IsNullOrEmpty(theme) && theme != "theme-base.css")
            {
                head.Append($"    <link rel=\"stylesheet\" href=\"/assets/{WebUtility.HtmlEncode(theme)}\" />\n");
            }

            // Inject everything before </head>
            html = html.Replace("</head>", head.ToString() + "  </head>");

            // Write the generated file
            var profilesDir = Path.Combine(_env.WebRootPath, "profiles");
            Directory.CreateDirectory(profilesDir);

            var outputPath = Path.Combine(profilesDir, $"{userSlug}.html");
            await File.WriteAllTextAsync(outputPath, html, new UTF8Encoding(false));

            _logger.LogInformation("Generated static profile for {UserSlug} at {Path}", userSlug, outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate static profile for {UserSlug}", userSlug);
        }
    }

    /// <summary>
    /// Deletes the static profile HTML for the given user (e.g. on account deletion).
    /// </summary>
    public void Delete(string userSlug)
    {
        var path = Path.Combine(_env.WebRootPath, "profiles", $"{userSlug}.html");
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted static profile for {UserSlug}", userSlug);
        }
    }
}
