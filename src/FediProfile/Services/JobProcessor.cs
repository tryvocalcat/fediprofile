using System.Text.Json;
using Microsoft.Extensions.Logging;
using FediProfile.Models;

namespace FediProfile.Services;

/// <summary>
/// Processes queued ActivityPub jobs from the domain-level Jobs table.
/// Iterates over configured domains, dequeues pending jobs, and dispatches
/// them to the appropriate handler (Follow, Undo-Follow, Create fan-out).
/// </summary>
public class JobProcessor
{
    private readonly string[] _domains;
    private readonly int _maxJobsPerRun;
    private readonly LocalDbFactory _factory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<JobProcessor> _logger;

    public JobProcessor(
        IConfiguration configuration,
        LocalDbFactory factory,
        IServiceProvider serviceProvider,
        ILogger<JobProcessor> logger)
    {
        _domains = configuration.GetSection("Domains").Get<string[]>() ?? new[] { "localhost" };
        _maxJobsPerRun = int.TryParse(Environment.GetEnvironmentVariable("MAX_JOBS_PER_RUN"), out var m) ? m : 5;
        _factory = factory;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<int> DoWorkAsync(CancellationToken stoppingToken)
    {
        var jobsProcessed = 0;

        foreach (var domainRaw in _domains)
        {
            var domain = domainRaw.Split(':')[0];
            _logger.LogInformation("Processing jobs for domain: {Domain}", domain);

            try
            {
                jobsProcessed += await ProcessNextQueueWorkAsync(domain, domainRaw, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing queue work for domain {Domain}", domain);
            }
        }

        return jobsProcessed;
    }

    private async Task<int> ProcessNextQueueWorkAsync(string domain, string fullDomain, CancellationToken stoppingToken)
    {
        var domainDb = _factory.GetInstance(domain);
        var jobQueue = new JobQueueService(domainDb, _serviceProvider.GetService<ILogger<JobQueueService>>());

        _logger.LogInformation("Processing up to {MaxJobs} queue jobs for domain {Domain}", _maxJobsPerRun, domain);

        var count = 0;

        for (int i = 0; i < _maxJobsPerRun; i++)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var job = await jobQueue.GetNextJobAsync();

            if (job == null)
            {
                _logger.LogInformation("No more jobs in queue for domain {Domain} after processing {Count} jobs", domain, i);
                break;
            }

            _logger.LogInformation("Found queue job {JobId} of type {JobType} for processing ({Num}/{Max})",
                job.Id, job.JobType, i + 1, _maxJobsPerRun);

            count++;

            try
            {
                switch (job.JobType)
                {
                    case "follow":
                        await ProcessFollowJob(job, domain, fullDomain, jobQueue);
                        break;
                    case "undo_follow":
                        await ProcessUndoFollowJob(job, domain, fullDomain, jobQueue);
                        break;
                    case "create":
                        await ProcessCreateJob(job, domain, fullDomain, jobQueue);
                        break;
                    default:
                        _logger.LogWarning("Unknown job type {JobType} for job {JobId}", job.JobType, job.Id);
                        await jobQueue.AddJobLogAsync(job.Id, $"Unknown job type: {job.JobType}");
                        await jobQueue.FailJobAsync(job.Id, $"Unknown job type: {job.JobType}", false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process job {JobId} of type {JobType}", job.Id, job.JobType);
                await jobQueue.AddJobLogAsync(job.Id, $"FAILED: {ex.Message}");
                await jobQueue.FailJobAsync(job.Id, ex.Message);
            }
        }

        return count;
    }

    /// <summary>
    /// Processes a Follow activity: deserializes the InboxMessage from the payload,
    /// resolves the target user, and delegates to FollowService.HandleFollowAsync.
    /// </summary>
    private async Task ProcessFollowJob(SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        try
        {
            var inboxMsg = DeserializePayload(job);

            await jobQueue.AddJobLogAsync(job.Id, $"Processing Follow from actor: {inboxMsg.Actor}");

            var targetActorId = inboxMsg.Object?.ToString();
            if (targetActorId == null)
            {
                throw new InvalidOperationException("Follow activity missing target object");
            }

            var userSlug = ExtractUserSlugFromActorId(targetActorId, fullDomain);
            if (userSlug == null)
            {
                throw new InvalidOperationException($"Could not extract user slug from target actor ID {targetActorId}");
            }

            var scheme = fullDomain.Contains("localhost") ? "http" : "https";
            var db = _factory.GetInstance(domain, userSlug);
            var actorId = $"{scheme}://{fullDomain}/{userSlug}";

            var followService = _serviceProvider.GetRequiredService<FollowService>();
            await followService.HandleFollowAsync(inboxMsg, db, actorId);

            await jobQueue.AddJobLogAsync(job.Id, $"Successfully processed Follow from {inboxMsg.Actor} for user {userSlug}");
            await jobQueue.CompleteJobAsync(job.Id);
            _logger.LogInformation("Successfully processed Follow job {JobId} from {Actor}", job.Id, inboxMsg.Actor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Follow job {JobId}", job.Id);
            await jobQueue.AddJobLogAsync(job.Id, $"FAILED: {ex.Message}");
            await jobQueue.FailJobAsync(job.Id, ex.Message);
        }
    }

    /// <summary>
    /// Processes an Undo-Follow activity: deserializes the InboxMessage from the payload,
    /// resolves the target user, and delegates to FollowService.HandleUnfollowAsync.
    /// </summary>
    private async Task ProcessUndoFollowJob(SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        try
        {
            var inboxMsg = DeserializePayload(job);

            await jobQueue.AddJobLogAsync(job.Id, $"Processing Undo-Follow from actor: {inboxMsg.Actor}");

            var followObject = inboxMsg.GetFollowObject();
            if (followObject == null)
            {
                throw new InvalidOperationException("Undo activity missing inner Follow object");
            }

            var userSlug = ExtractUserSlugFromActorId(followObject, fullDomain);
            if (userSlug == null)
            {
                throw new InvalidOperationException($"Could not extract user slug from Follow object {followObject}");
            }

            var db = _factory.GetInstance(domain, userSlug);

            var followService = _serviceProvider.GetRequiredService<FollowService>();
            await followService.HandleUnfollowAsync(inboxMsg, db);

            await jobQueue.AddJobLogAsync(job.Id, $"Successfully processed Undo-Follow from {inboxMsg.Actor} for user {userSlug}");
            await jobQueue.CompleteJobAsync(job.Id);
            _logger.LogInformation("Successfully processed Undo-Follow job {JobId} from {Actor}", job.Id, inboxMsg.Actor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Undo-Follow job {JobId}", job.Id);
            await jobQueue.AddJobLogAsync(job.Id, $"FAILED: {ex.Message}");
            await jobQueue.FailJobAsync(job.Id, ex.Message);
        }
    }

    /// <summary>
    /// Processes a Create activity: deserializes the InboxMessage from the payload,
    /// fans out to all local users that follow the actor, and delegates to
    /// AnnounceService.SendAnnounceAsync for each.
    /// </summary>
    private async Task ProcessCreateJob(SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        try
        {
            var inboxMsg = DeserializePayload(job);

            await jobQueue.AddJobLogAsync(job.Id, $"Processing Create from actor: {inboxMsg.Actor}");

            var scheme = fullDomain.Contains("localhost") ? "http" : "https";
            var mainDb = _factory.GetInstance(domain);
            var localFollowers = await mainDb.GetFollowersOfActorAsync(inboxMsg.Actor ?? "");

            if (localFollowers.Count == 0)
            {
                await jobQueue.AddJobLogAsync(job.Id, $"No local followers for actor {inboxMsg.Actor}, skipping");
                await jobQueue.CompleteJobAsync(job.Id);
                _logger.LogInformation("Create job {JobId}: no local followers for {Actor}", job.Id, inboxMsg.Actor);
                return;
            }

            await jobQueue.AddJobLogAsync(job.Id, $"Fan-out Create from {inboxMsg.Actor} to {localFollowers.Count} local user(s)");
            _logger.LogInformation("Create job {JobId}: fan-out to {Count} local user(s)", job.Id, localFollowers.Count);

            var announceService = _serviceProvider.GetRequiredService<AnnounceService>();

            foreach (var userSlug in localFollowers)
            {
                try
                {
                    var userDb = _factory.GetInstance(domain, userSlug);
                    var actorId = $"{scheme}://{fullDomain}/{userSlug}";
                    await announceService.SendAnnounceAsync(inboxMsg, userDb, actorId);
                    await jobQueue.AddJobLogAsync(job.Id, $"Announced to user {userSlug}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Create job {JobId}: error for user {UserSlug}", job.Id, userSlug);
                    await jobQueue.AddJobLogAsync(job.Id, $"Error for user {userSlug}: {ex.Message}");
                }
            }

            await jobQueue.CompleteJobAsync(job.Id);
            _logger.LogInformation("Successfully processed Create job {JobId} from {Actor}", job.Id, inboxMsg.Actor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Create job {JobId}", job.Id);
            await jobQueue.AddJobLogAsync(job.Id, $"FAILED: {ex.Message}");
            await jobQueue.FailJobAsync(job.Id, ex.Message);
        }
    }

    private static InboxMessage DeserializePayload(SimpleJob job)
    {
        if (string.IsNullOrEmpty(job.Payload))
        {
            throw new InvalidOperationException("Job payload is empty");
        }

        var message = JsonSerializer.Deserialize<InboxMessage>(job.Payload);
        if (message == null)
        {
            throw new InvalidOperationException("Failed to deserialize InboxMessage from job payload");
        }

        return message;
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
                if (!uri.Host.Equals(expectedDomain.Split(':')[0], StringComparison.OrdinalIgnoreCase))
                    return null;

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
