using System.Text;
using System.Text.Json;
using FediProfile.Models;

namespace FediProfile.Services;

public class ActorService
{
    private static readonly JsonSerializerOptions _actorJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ActorService> _logger;

    public ActorService(IWebHostEnvironment env, ILogger<ActorService> logger)
    {
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Returns the shared JsonSerializerOptions for actor JSON serialization.
    /// </summary>
    public static JsonSerializerOptions ActorJsonOptions => _actorJsonOptions;

    public async Task<ActivityPubActor> BuildActorAsync(UserScopedDb db, HttpRequest request, string? userSlug = null)
    {
        var domain = request.Host.Host;
        if (request.Host.Port.HasValue && request.Host.Port != 80 && request.Host.Port != 443)
        {
            domain = $"{domain}:{request.Host.Port}";
        }
        var scheme = request.Scheme;
        var slug = userSlug ?? request.RouteValues["userSlug"]?.ToString() ?? "profile";
        var baseDomain = $"{scheme}://{domain}";
        return await BuildActorCoreAsync(db, slug, baseDomain);
    }

    /// <summary>
    /// Builds the ActivityPub actor object without requiring an HttpRequest.
    /// Used both for live requests and for static file generation.
    /// </summary>
    public async Task<ActivityPubActor> BuildActorCoreAsync(UserScopedDb db, string slug, string baseDomain)
    {
        // Get username and other settings from database
        var username = await db.GetActorUsernameAsync();
        var baseId = $"{baseDomain}/{slug}";
        var bio = await db.GetActorBioAsync();  
        var avatarUrl = await db.GetActorAvatarUrlAsync();
        if (!string.IsNullOrEmpty(avatarUrl) && avatarUrl.StartsWith("/"))
            avatarUrl = $"{baseDomain}{avatarUrl}";

        // Get actor keys for publicKey field
        var (pubKey, privKey) = await db.GetActorKeysAsync();
        var theme = await db.GetUiThemeAsync();

        var links = await db.GetLinksAsync(false);
        // Filter out hidden links and format as HTML attachments
        var attachments = links
            .Select(link => 
            {
                var url = link.Url;
                var name = link.Name;
                var icon = link.Icon;
                if (!string.IsNullOrEmpty(icon) && icon.StartsWith("/"))
                    icon = $"{baseDomain}{icon}";

                string displayUrl = url;

                try {
                    // Validate URL
                    var uri = new Uri(url);
                    displayUrl = uri.Host + uri.PathAndQuery;
                }
                catch
                {
                    // If URL is invalid, skip this link
                }

                var invisibleStart = displayUrl.Length > 30 ? 
                    $"<span class=\"invisible\">https://</span><span class=\"ellipsis\">{displayUrl.Substring(0, 30)}</span>" :
                    $"<span class=\"invisible\">https://</span><span class=\"\">{displayUrl}</span>";
                
                var htmlValue = $"<a href=\"{url}\" target=\"_blank\" rel=\"nofollow noopener me\" translate=\"no\">{invisibleStart}<span class=\"invisible\"></span></a>";
                
                return new LinkAttachment
                {
                    Name = name,
                    Value = htmlValue,
                    Href = url,
                    Icon = !string.IsNullOrEmpty(icon) ? new ActivityPubImage { Type = "Image", Url = icon } : null,
                    Category = link.Category,
                    Description = link.Description,
                    AutoBoost = link.AutoBoost ? true : null
                };
            })
            .ToList();

        return new ActivityPubActor
        {
            Context = "https://www.w3.org/ns/activitystreams",
            Id = baseId,
            Type = "Person",
            PreferredUsername = slug,
            Name = username,
            Summary = bio,
            Url = baseId,
            Inbox = $"{baseDomain}/sharedInbox",
            Outbox = $"{baseId}/outbox",
            Followers = $"{baseId}/followers",
            Following = $"{baseId}/following",
            PublicKey = new PublicKeyDefinition
            {
                Id = $"{baseId}#main-key",
                Owner = baseId,
                PublicKeyPem = pubKey ?? string.Empty
            },
            Discoverable = true,
            Icon = new ActivityPubImage
            {
                Type = "Image",
                MediaType = "image/png",
                Url = avatarUrl
            },
            Image = new ActivityPubImage
            {
                Type = "Image",
                MediaType = "image/png",
                Url = avatarUrl
            },
            Attachment = attachments.Count > 0 ? attachments : null,
            FediProfile = new FediProfileExtension
            {
                Theme = theme
            }
        };
    }

    /// <summary>
    /// (Re)generates the static actor JSON file for the given user.
    /// Stored at wwwroot/profiles/{userSlug}.json.
    /// Called when the user saves their profile or changes links,
    /// mirroring the ProfileHtmlService pattern.
    /// </summary>
    public async Task GenerateActorJsonAsync(UserScopedDb userDb, string userSlug, string baseDomain)
    {
        try
        {
            var actor = await BuildActorCoreAsync(userDb, userSlug, baseDomain);
            var json = JsonSerializer.Serialize(actor, _actorJsonOptions);

            var profilesDir = Path.Combine(_env.WebRootPath, "profiles");
            Directory.CreateDirectory(profilesDir);

            var outputPath = Path.Combine(profilesDir, $"{userSlug}.json");
            await File.WriteAllTextAsync(outputPath, json, new UTF8Encoding(false));

            _logger.LogInformation("Generated static actor JSON for {UserSlug} at {Path}", userSlug, outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate static actor JSON for {UserSlug}", userSlug);
        }
    }

    /// <summary>
    /// Deletes the static actor JSON for the given user (e.g. on account deletion).
    /// </summary>
    public void DeleteActorJson(string userSlug)
    {
        var path = Path.Combine(_env.WebRootPath, "profiles", $"{userSlug}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted static actor JSON for {UserSlug}", userSlug);
        }
    }
}
