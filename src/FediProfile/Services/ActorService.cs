using FediProfile.Models;

namespace FediProfile.Services;

public class ActorService
{
    public async Task<ActivityPubActor> BuildActorAsync(UserScopedDb db, HttpRequest request, string? userSlug = null)
    {
        // Get username and other settings from database
        var username = await db.GetActorUsernameAsync();
        var domain = request.Host.Host;
        
        if (request.Host.Port.HasValue && request.Host.Port != 80 && request.Host.Port != 443)
        {
            domain = $"{domain}:{request.Host.Port}";
        }

        var scheme = request.Scheme;
        
        // Use userSlug for the actor ID if available, otherwise fall back to route value or "profile"
        var slug = userSlug ?? request.RouteValues["userSlug"]?.ToString() ?? "profile";
        var baseDomain = $"{scheme}://{domain}";
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
}
