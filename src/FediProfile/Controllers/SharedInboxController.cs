using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using FediProfile.Models;
using FediProfile.Services;

namespace FediProfile.Controllers;

/// <summary>
/// Shared inbox endpoint (/sharedInbox) that receives activities once
/// and fans them out to all relevant local users via the domain-level
/// Following index table, avoiding per-user database iteration.
/// </summary>
[ApiController]
[Route("sharedInbox")]
public class SharedInboxController : ControllerBase
{
    private readonly ILogger<SharedInboxController> _logger;
    private readonly FollowService _followService;
    private readonly AnnounceService _announceService;
    private readonly LocalDbFactory _factory;
    private readonly ActorService _actorService;

    public SharedInboxController(
        ILogger<SharedInboxController> logger,
        FollowService followService,
        AnnounceService announceService,
        LocalDbFactory factory,
        ActorService actorService)
    {
        _logger = logger;
        _followService = followService;
        _announceService = announceService;
        _factory = factory;
        _actorService = actorService;
    }

    [HttpPost]
    public async Task<IActionResult> PostSharedInbox([FromBody] JsonElement? message)
    {
        if (message == null)
        {
            _logger.LogWarning("Shared inbox received null message");
            return BadRequest("Invalid message content");
        }

        var domain = HttpContext.Request.Host.Host;
        var scheme = HttpContext.Request.Scheme;
        var fullDomain = HttpContext.Request.Host.ToString();

        try
        {
            var inboxMsg = JsonSerializer.Deserialize<InboxMessage>(message.Value.GetRawText());

            if (inboxMsg == null)
            {
                _logger.LogWarning("Shared inbox: could not deserialize message");
                return BadRequest("Invalid message format");
            }

            _logger.LogInformation("Shared inbox received Activity: {Type} from {Actor}", inboxMsg.Type, inboxMsg.Actor);

            if (inboxMsg.IsFollow())
            {
                // Follow activities are user-addressed; extract the target user from the object
                var targetActorId = inboxMsg.Object?.ToString();
                if (targetActorId != null)
                {
                    // Try to parse the target user slug from the actor ID (e.g., https://domain/userSlug)
                    var userSlug = ExtractUserSlugFromActorId(targetActorId, fullDomain);
                    if (userSlug != null)
                    {
                        var db = _factory.GetInstance(domain, userSlug);
                        var actorId = $"{scheme}://{fullDomain}/{userSlug}";
                        await _followService.HandleFollowAsync(inboxMsg, db, actorId);
                    }
                    else
                    {
                        Console.WriteLine($"Shared inbox: could not extract user slug from target actor ID {targetActorId}");
                        _logger.LogWarning("Shared inbox: could not determine target user for Follow from {Actor}", inboxMsg.Actor);
                    }
                }
            }
            else if (inboxMsg.IsUndo() && inboxMsg.GetFollowActor() != null)
            {
                // Undo Follow — extract target from the inner Follow object
                var followObject = inboxMsg.GetFollowObject();
                if (followObject != null)
                {
                    var userSlug = ExtractUserSlugFromActorId(followObject, fullDomain);
                    if (userSlug != null)
                    {
                        var db = _factory.GetInstance(domain, userSlug);
                        await _followService.HandleUnfollowAsync(inboxMsg, db);
                    }
                }
            }
            else if (inboxMsg.IsCreate())
            {
                Console.WriteLine($"Raw message object: {message.Value.GetRawText()}");
                
                // Fan-out: look up which local users follow this actor
                var mainDb = _factory.GetInstance(domain);
                var localFollowers = await mainDb.GetFollowersOfActorAsync(inboxMsg.Actor ?? "");

                if (localFollowers.Count == 0)
                {
                    _logger.LogInformation("Shared inbox: no local followers for actor {Actor}, ignoring Create", inboxMsg.Actor);
                    return Ok();
                }

                _logger.LogInformation("Shared inbox: fan-out Create from {Actor} to {Count} local user(s)", inboxMsg.Actor, localFollowers.Count);

                foreach (var userSlug in localFollowers)
                {
                    try
                    {
                        var userDb = _factory.GetInstance(domain, userSlug);
                        var actorId = $"{scheme}://{fullDomain}/{userSlug}";
                        await _announceService.SendAnnounceAsync(inboxMsg, userDb, actorId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Shared inbox: error processing Create for user {UserSlug}", userSlug);
                    }
                }
            }
            else if (inboxMsg.IsAnnounce())
            {
                _logger.LogInformation("Shared inbox: received Announce from {Actor}, ignoring", inboxMsg.Actor);
            }
            else
            {
                _logger.LogInformation("Shared inbox: ignoring activity type {Type}", inboxMsg.Type);
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing shared inbox message");
            return StatusCode(500, "Error processing message");
        }
    }

    /// <summary>
    /// Extracts the user slug from an actor ID URL.
    /// For example, "https://example.com/maho" → "maho"
    /// </summary>
    private static string? ExtractUserSlugFromActorId(string actorId, string expectedDomain)
    {
        try
        {
            if (Uri.TryCreate(actorId, UriKind.Absolute, out var uri))
            {
                // Verify this actor belongs to our domain
                if (!uri.Host.Equals(expectedDomain.Split(':')[0], StringComparison.OrdinalIgnoreCase))
                    return null;

                // The slug is the first path segment: /userSlug
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                if (segments.Length >= 1 && !string.IsNullOrEmpty(segments[0]))
                    return segments[0];
            }
        }
        catch
        {
            // Invalid URI
        }

        return null;
    }
}
