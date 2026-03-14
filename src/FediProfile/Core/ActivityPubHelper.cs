using System.Net.Http.Headers;
using System.Text.Json;

namespace FediProfile.Core;

public static class ActivityPubHelper
{
    public static bool IsActivityPubRequest(string acceptHeader)
    {
        if (string.IsNullOrWhiteSpace(acceptHeader))
            return false;

        var accept = acceptHeader.ToLowerInvariant();
        return accept.Contains("application/activity+json")
            || accept.Contains("application/ld+json")
            || accept.Contains("application/json");
    }

    /// <summary>
    /// Resolved issuer details from badge JSON-LD data.
    /// </summary>
    public record IssuerInfo(string Name, string ActorUrl, string? Avatar, string? Bio);

    /// <summary>
    /// Resolves badge issuer details by probing the assertion's issuer.url (or issuer.id)
    /// as an ActivityPub actor, falling back to the Note's attributedTo, and finally
    /// to the provided fallback values.
    /// </summary>
    public static async Task<IssuerInfo> ResolveIssuerAsync(
        JsonElement noteRoot,
        string? fallbackActorUrl = null,
        HttpClient? httpClient = null)
    {
        var disposeHttp = httpClient == null;
        httpClient ??= CreateActivityPubHttpClient();

        try
        {
            string? issuerName = null;
            string? issuerActorUrl = null;
            string? issuerAvatar = null;
            string? issuerBio = null;

            // Collect candidate URLs to probe (issuer.url, issuer.id, attributedTo)
            string? issuerOBUrl = null;  // OpenBadges issuer "url" (may be AP actor)
            string? issuerOBId = null;   // OpenBadges issuer "id"

            // 1. Try to extract issuer info from assertion attachment's issuer object
            if (noteRoot.TryGetProperty("attachment", out var attachments) &&
                attachments.ValueKind == JsonValueKind.Array)
            {
                foreach (var attachment in attachments.EnumerateArray())
                {
                    if (!attachment.TryGetProperty("type", out var typeEl) ||
                        !string.Equals(typeEl.GetString(), "Assertion", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!attachment.TryGetProperty("issuer", out var issuerEl))
                        continue;

                    // issuer can be an object with name, url, id, email
                    if (issuerEl.ValueKind == JsonValueKind.Object)
                    {
                        issuerName = issuerEl.TryGetProperty("name", out var n) ? n.GetString() : null;
                        issuerOBUrl = issuerEl.TryGetProperty("url", out var u) ? u.GetString() : null;
                        issuerOBId = issuerEl.TryGetProperty("id", out var i) ? i.GetString() : null;
                    }
                    else if (issuerEl.ValueKind == JsonValueKind.String)
                    {
                        issuerOBUrl = issuerEl.GetString();
                    }

                    break; // only need the first assertion's issuer
                }
            }

            // 2. Build the candidate list: issuer.url, issuer.id, attributedTo, fallback
            var attributedTo = noteRoot.TryGetProperty("attributedTo", out var attrEl)
                ? attrEl.GetString() : null;

            // Determine the primary actor URL (prefer issuer.url > attributedTo > issuer.id > fallback)
            issuerActorUrl = issuerOBUrl ?? attributedTo ?? issuerOBId ?? fallbackActorUrl ?? "";

            // 3. Probe candidate URLs as ActivityPub actors to get rich details
            //    Try each unique candidate until we get a successful AP response
            var candidates = new[] { issuerOBUrl, attributedTo, issuerOBId, fallbackActorUrl }
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var candidateUrl in candidates)
            {
                try
                {
                    var actorResponse = await httpClient.GetAsync(candidateUrl);
                    if (!actorResponse.IsSuccessStatusCode)
                        continue;

                    var actorJson = await actorResponse.Content.ReadAsStringAsync();
                    using var actorDoc = JsonDocument.Parse(actorJson);
                    var actor = actorDoc.RootElement;

                    // Check if this looks like an AP actor (has type, inbox, or preferredUsername)
                    var hasActorType = actor.TryGetProperty("type", out var actorTypeEl) &&
                        (string.Equals(actorTypeEl.GetString(), "Person", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(actorTypeEl.GetString(), "Service", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(actorTypeEl.GetString(), "Application", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(actorTypeEl.GetString(), "Group", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(actorTypeEl.GetString(), "Organization", StringComparison.OrdinalIgnoreCase));
                    var hasInbox = actor.TryGetProperty("inbox", out _);

                    if (!hasActorType && !hasInbox)
                        continue; // Not an AP actor, try next candidate

                    // Use this candidate as the actor URL since it resolved as AP
                    issuerActorUrl = candidateUrl!;

                    // name / preferredUsername
                    if (actor.TryGetProperty("name", out var actorNameEl) &&
                        !string.IsNullOrWhiteSpace(actorNameEl.GetString()))
                        issuerName = actorNameEl.GetString();
                    else if (actor.TryGetProperty("preferredUsername", out var prefEl) &&
                             !string.IsNullOrWhiteSpace(prefEl.GetString()))
                        issuerName ??= prefEl.GetString();

                    // icon (avatar) — can be string or { type: "Image", url: "..." }
                    if (actor.TryGetProperty("icon", out var iconEl))
                    {
                        if (iconEl.ValueKind == JsonValueKind.String)
                            issuerAvatar = iconEl.GetString();
                        else if (iconEl.ValueKind == JsonValueKind.Object &&
                                 iconEl.TryGetProperty("url", out var iconUrlEl))
                            issuerAvatar = iconUrlEl.GetString();
                    }

                    // summary (bio)
                    if (actor.TryGetProperty("summary", out var summaryEl))
                        issuerBio = summaryEl.GetString();

                    break; // Successfully resolved from this candidate
                }
                catch
                {
                    // This candidate failed, try the next one
                }
            }

            // Final defaults
            issuerName ??= issuerActorUrl;

            return new IssuerInfo(issuerName ?? "Unknown", issuerActorUrl, issuerAvatar, issuerBio);
        }
        finally
        {
            if (disposeHttp)
                httpClient.Dispose();
        }
    }

    /// <summary>
    /// Creates an HttpClient configured with proper Accept headers for ActivityPub content negotiation.
    /// </summary>
    public static HttpClient CreateActivityPubHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json", 0.9));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.8));
        return http;
    }
}
