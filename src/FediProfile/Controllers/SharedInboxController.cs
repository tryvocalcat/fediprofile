using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using FediProfile.Models;
using FediProfile.Services;

namespace FediProfile.Controllers;

/// <summary>
/// Shared inbox endpoint (/sharedInbox) that receives ActivityPub activities
/// and enqueues them into the domain-level Jobs table for background processing
/// by the JobExecutor / JobProcessor pipeline.
/// </summary>
[ApiController]
[Route("sharedInbox")]
public class SharedInboxController : ControllerBase
{
    private readonly ILogger<SharedInboxController> _logger;
    private readonly LocalDbFactory _factory;

    public SharedInboxController(
        ILogger<SharedInboxController> logger,
        LocalDbFactory factory)
    {
        _logger = logger;
        _factory = factory;
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

        try
        {
            var rawJson = message.Value.GetRawText();
            var inboxMsg = JsonSerializer.Deserialize<InboxMessage>(rawJson);

            if (inboxMsg == null)
            {
                _logger.LogWarning("Shared inbox: could not deserialize message");
                return BadRequest("Invalid message format");
            }

            _logger.LogInformation("Shared inbox received Activity: {Type} from {Actor}", inboxMsg.Type, inboxMsg.Actor);

            var domainDb = _factory.GetInstance(domain);
            var jobQueue = new JobQueueService(domainDb);

            string? jobType = null;

            if (inboxMsg.IsFollow())
            {
                jobType = "follow";
            }
            else if (inboxMsg.IsUndo() && inboxMsg.GetFollowActor() != null)
            {
                jobType = "undo_follow";
            }
            else if (inboxMsg.IsCreate())
            {
                jobType = "create";
            }
            else if (inboxMsg.IsAnnounce())
            {
                _logger.LogInformation("Shared inbox: received Announce from {Actor}, ignoring", inboxMsg.Actor);
            }
            else
            {
                _logger.LogInformation("Shared inbox: ignoring activity type {Type}", inboxMsg.Type);
            }

            if (jobType != null)
            {
                // Enqueue the raw InboxMessage JSON as the job payload
                var jobId = await jobQueue.AddJobAsync(
                    jobType: jobType,
                    payload: rawJson,
                    actorUri: inboxMsg.Actor,
                    createdBy: "SharedInboxController",
                    notes: $"{inboxMsg.Type} from {inboxMsg.Actor}");

                _logger.LogInformation("Shared inbox: enqueued {JobType} job {JobId} from {Actor}",
                    jobType, jobId, inboxMsg.Actor);
            }

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing shared inbox message");
            return StatusCode(500, "Error processing message");
        }
    }
}
