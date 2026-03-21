using System.Text.Json;
using Microsoft.Extensions.Logging;
using FediProfile.Models;
using FediProfile.Core;
using System.Net.Http.Headers;

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
                    case "announce":
                        await ProcessAnnounceJob(job, domain, fullDomain, jobQueue);
                        break;
                    case "sync_badge_issuer":
                        await ProcessSyncBadgeIssuerJob(job, domain, fullDomain, jobQueue);
                        break;
                    case "fetch_badge_note":
                        await ProcessFetchBadgeNoteJob(job, domain, fullDomain, jobQueue);
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
    /// Processes a Create activity: deserializes the InboxMessage from the payload
    /// and delegates to the appropriate handler.
    /// </summary>
    private async Task ProcessCreateJob(SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        try
        {
            var inboxMsg = DeserializePayload(job);

            await jobQueue.AddJobLogAsync(job.Id, $"Processing Create from actor: {inboxMsg.Actor}");

            await ProcessAnnouncements(inboxMsg, job, domain, fullDomain, jobQueue);
            await ProcessBadges(inboxMsg, job, domain, fullDomain, jobQueue);

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

    /// <summary>
    /// Checks the Create activity's Note for OpenBadges Assertion attachments.
    /// For each assertion whose recipient identity URL matches a local user's verified URI,
    /// stores the badge in that user's DB.
    /// </summary>
    private async Task ProcessBadges(InboxMessage inboxMsg, SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        if (inboxMsg.Object is not JsonElement objectElement)
            return;

        // Extract all OpenBadge Assertion attachments using shared parser
        var assertions = BadgeParser.ExtractAssertions(objectElement);

        if (assertions.Count == 0)
            return;

        var mainDb = _factory.GetInstance(domain);

        var noteId = objectElement.TryGetProperty("id", out var noteIdEl)
            ? noteIdEl.GetString() ?? inboxMsg.Id : inboxMsg.Id;

        // Resolve issuer details by probing the assertion's issuer object and AP actor URLs
        var issuerInfo = await ActivityPubHelper.ResolveIssuerAsync(
            objectElement, fallbackActorUrl: inboxMsg.Actor);

        // Upsert issuer once for the whole batch
        int? issuerId = null;

        foreach (var assertion in assertions)
        {
            // Targeted lookup: only query users that own this specific verified URI
            var matchedSlugs = await mainDb.GetUserSlugsByVerifiedUriAsync(assertion.RecipientUri);
            if (matchedSlugs.Count == 0)
            {
                _logger.LogDebug("Create job {JobId}: assertion recipient {Recipient} does not match any verified URI",
                    job.Id, assertion.RecipientUri);
                continue;
            }

            // Lazy-init issuer on first match
            issuerId ??= await mainDb.UpsertBadgeIssuerAsync(
                issuerInfo.Name, issuerInfo.ActorUrl, issuerInfo.Avatar, issuerInfo.Bio);

            foreach (var userSlug in matchedSlugs)
            {
                try
                {
                    var userDb = _factory.GetInstance(domain, userSlug);
                    var badgeRecordId = await userDb.StoreBadgeAsync(
                        noteId, issuerId.Value, assertion.BadgeTitle, assertion.BadgeImage,
                        assertion.BadgeDescription, assertion.IssuedOn);

                    if (badgeRecordId > 0)
                    {
                        await jobQueue.AddJobLogAsync(job.Id, $"Stored badge '{assertion.BadgeTitle}' for user {userSlug} (record {badgeRecordId})");
                        _logger.LogInformation("Create job {JobId}: stored badge '{Title}' for user {UserSlug}", job.Id, assertion.BadgeTitle, userSlug);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Create job {JobId}: error storing badge for user {UserSlug}", job.Id, userSlug);
                    await jobQueue.AddJobLogAsync(job.Id, $"Error storing badge for user {userSlug}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Fans out a Create activity to all local users that follow the actor,
    /// delegating to AnnounceService.SendAnnounceAsync for each.
    /// After a successful announce, stores the post in the user's RecentPosts table.
    /// </summary>
    private async Task ProcessAnnouncements(InboxMessage inboxMsg, SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        var scheme = fullDomain.Contains("localhost") ? "http" : "https";
        var mainDb = _factory.GetInstance(domain);
        var localFollowers = await mainDb.GetFollowersOfActorAsync(inboxMsg.Actor ?? "");

        if (localFollowers.Count == 0)
        {
            await jobQueue.AddJobLogAsync(job.Id, $"No local followers for actor {inboxMsg.Actor}, skipping announcements");
            _logger.LogInformation("Create job {JobId}: no local followers for {Actor}", job.Id, inboxMsg.Actor);
            return;
        }

        await jobQueue.AddJobLogAsync(job.Id, $"Fan-out Create from {inboxMsg.Actor} to {localFollowers.Count} local user(s)");
        _logger.LogInformation("Create job {JobId}: fan-out to {Count} local user(s)", job.Id, localFollowers.Count);

        var announceService = _serviceProvider.GetRequiredService<AnnounceService>();

        // Check once whether this Create activity is a reply (has inReplyTo)
        var isReply = inboxMsg.IsReply();

        // Extract post metadata once from the Note object (shared across all users)
        var (noteId, noteContent, noteSummary, noteUrl, notePublished, noteActorName, noteActorAvatar) = ExtractNoteMetadata(inboxMsg);

        foreach (var userSlug in localFollowers)
        {
            try
            {
                var userDb = _factory.GetInstance(domain, userSlug);

                // If the user has SkipReplies enabled and this is a reply, skip announcing
                if (isReply)
                {
                    var settings = await userDb.GetSettingsAsync();
                    if (settings?.SkipReplies == true)
                    {
                        await jobQueue.AddJobLogAsync(job.Id, $"Skipping reply announcement for user {userSlug} (SkipReplies enabled)");
                        _logger.LogInformation("Create job {JobId}: skipping reply for user {UserSlug} (SkipReplies)", job.Id, userSlug);
                        continue;
                    }
                }

                var actorId = $"{scheme}://{fullDomain}/{userSlug}";
                await announceService.SendAnnounceAsync(inboxMsg, userDb, actorId, jobQueue, job.Id);
                await jobQueue.AddJobLogAsync(job.Id, $"Announced to user {userSlug}");

                // Store in RecentPosts for the public profile feed
                if (!string.IsNullOrEmpty(noteId))
                {
                    try
                    {
                        await userDb.StoreRecentPostAsync(
                            noteId: noteId,
                            actorUri: inboxMsg.Actor ?? "",
                            actorName: noteActorName,
                            actorAvatar: noteActorAvatar,
                            content: noteContent,
                            summary: noteSummary,
                            url: noteUrl,
                            publishedUtc: notePublished);
                    }
                    catch (Exception storeEx)
                    {
                        _logger.LogWarning(storeEx, "Create job {JobId}: failed to store recent post for {UserSlug}", job.Id, userSlug);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create job {JobId}: error for user {UserSlug}", job.Id, userSlug);
                await jobQueue.AddJobLogAsync(job.Id, $"Error for user {userSlug}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Extracts metadata from a Create activity's inner Note object for storage in RecentPosts.
    /// </summary>
    private static (string? NoteId, string? Content, string? Summary, string? Url, string? Published, string? ActorName, string? ActorAvatar) ExtractNoteMetadata(InboxMessage inboxMsg)
    {
        if (inboxMsg.Object is not JsonElement obj || obj.ValueKind != JsonValueKind.Object)
            return (inboxMsg.Id, null, null, null, null, null, null);

        var noteId = obj.TryGetProperty("id", out var idEl) ? idEl.GetString() : inboxMsg.Id;
        var content = obj.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
        var summary = obj.TryGetProperty("summary", out var summaryEl) ? summaryEl.GetString() : null;

        // url can be a string or an object with href
        string? url = null;
        if (obj.TryGetProperty("url", out var urlEl))
        {
            if (urlEl.ValueKind == JsonValueKind.String)
                url = urlEl.GetString();
            else if (urlEl.ValueKind == JsonValueKind.Object && urlEl.TryGetProperty("href", out var hrefEl))
                url = hrefEl.GetString();
        }
        url ??= noteId; // fallback to the Note ID which is typically a URL

        var published = obj.TryGetProperty("published", out var pubEl) ? pubEl.GetString() : null;

        // Try to get actor display info from attributedTo or icon
        string? actorName = null;
        string? actorAvatar = null;
        if (obj.TryGetProperty("attributedTo", out var attrEl))
        {
            if (attrEl.ValueKind == JsonValueKind.String)
            {
                // Just the URI — name will come from the actor URI itself
            }
            else if (attrEl.ValueKind == JsonValueKind.Object)
            {
                actorName = attrEl.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                if (attrEl.TryGetProperty("icon", out var iconEl))
                {
                    if (iconEl.ValueKind == JsonValueKind.String)
                        actorAvatar = iconEl.GetString();
                    else if (iconEl.ValueKind == JsonValueKind.Object && iconEl.TryGetProperty("url", out var iconUrlEl))
                        actorAvatar = iconUrlEl.GetString();
                }
            }
        }

        return (noteId, content, summary, url, published, actorName, actorAvatar);
    }

    /// <summary>
    /// Processes an Announce activity received via shared inbox.
    /// Fetches the announced object URL, then runs badge extraction on it.
    /// </summary>
    private async Task ProcessAnnounceJob(SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        try
        {
            var inboxMsg = DeserializePayload(job);
            await jobQueue.AddJobLogAsync(job.Id, $"Processing Announce from actor: {inboxMsg.Actor}");

            // The object of an Announce is typically a URL string
            var objectUrl = inboxMsg.Object is JsonElement el && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : inboxMsg.Object?.ToString();

            if (string.IsNullOrEmpty(objectUrl))
            {
                await jobQueue.AddJobLogAsync(job.Id, "Announce has no object URL, skipping");
                await jobQueue.CompleteJobAsync(job.Id);
                return;
            }

            await jobQueue.AddJobLogAsync(job.Id, $"Fetching announced object: {objectUrl}");

            // Fetch the announced object
            var noteJson = await FetchActivityPubObjectAsync(objectUrl);
            if (noteJson == null)
            {
                throw new InvalidOperationException($"Failed to fetch announced object: {objectUrl}");
            }

            // Build a synthetic Create wrapping the fetched note for badge processing
            var syntheticCreate = new InboxMessage
            {
                Type = "Create",
                Id = inboxMsg.Id,
                Actor = inboxMsg.Actor,
                Object = JsonSerializer.Deserialize<JsonElement>(noteJson)
            };

            await ProcessBadges(syntheticCreate, job, domain, fullDomain, jobQueue);
            await jobQueue.CompleteJobAsync(job.Id);
            _logger.LogInformation("Successfully processed Announce job {JobId} from {Actor}", job.Id, inboxMsg.Actor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Announce job {JobId}", job.Id);
            await jobQueue.AddJobLogAsync(job.Id, $"FAILED: {ex.Message}");
            await jobQueue.FailJobAsync(job.Id, ex.Message);
        }
    }

    /// <summary>
    /// Processes a sync_badge_issuer job: fetches the actor's outbox, pages through it,
    /// and enqueues a fetch_badge_note job for each Announce's object URL.
    /// </summary>
    private async Task ProcessSyncBadgeIssuerJob(SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        try
        {
            // Payload is the actor URL
            var actorUrl = job.Payload?.Trim('"');
            if (string.IsNullOrEmpty(actorUrl))
                throw new InvalidOperationException("sync_badge_issuer job has no actor URL payload");

            await jobQueue.AddJobLogAsync(job.Id, $"Syncing badge issuer outbox: {actorUrl}");

            // 1. Fetch the actor to get outbox URL
            var actorJson = await FetchActivityPubObjectAsync(actorUrl);
            if (actorJson == null)
                throw new InvalidOperationException($"Failed to fetch actor: {actorUrl}");

            using var actorDoc = JsonDocument.Parse(actorJson);
            if (!actorDoc.RootElement.TryGetProperty("outbox", out var outboxEl))
                throw new InvalidOperationException($"Actor has no outbox property: {actorUrl}");

            var outboxUrl = outboxEl.GetString();
            if (string.IsNullOrEmpty(outboxUrl))
                throw new InvalidOperationException("Actor outbox URL is empty");

            await jobQueue.AddJobLogAsync(job.Id, $"Fetching outbox: {outboxUrl}");

            // 2. Fetch the outbox collection to get the first page
            var outboxJson = await FetchActivityPubObjectAsync(outboxUrl);
            if (outboxJson == null)
                throw new InvalidOperationException($"Failed to fetch outbox: {outboxUrl}");

            using var outboxDoc = JsonDocument.Parse(outboxJson);
            var outboxRoot = outboxDoc.RootElement;

            string? pageUrl = null;
            if (outboxRoot.TryGetProperty("first", out var firstEl))
            {
                pageUrl = firstEl.ValueKind == JsonValueKind.String
                    ? firstEl.GetString()
                    : firstEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            }

            if (string.IsNullOrEmpty(pageUrl))
                throw new InvalidOperationException("Outbox has no 'first' page URL");

            int totalEnqueued = 0;

            // 3. Page through the outbox
            while (!string.IsNullOrEmpty(pageUrl))
            {
                await jobQueue.AddJobLogAsync(job.Id, $"Fetching outbox page: {pageUrl}");
                var pageJson = await FetchActivityPubObjectAsync(pageUrl);
                if (pageJson == null) break;

                using var pageDoc = JsonDocument.Parse(pageJson);
                var pageRoot = pageDoc.RootElement;

                if (pageRoot.TryGetProperty("orderedItems", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        // Each item should be an Announce with an object URL
                        var itemType = item.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                        if (!string.Equals(itemType, "Announce", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string? objectUrl = null;
                        if (item.TryGetProperty("object", out var objEl))
                        {
                            objectUrl = objEl.ValueKind == JsonValueKind.String
                                ? objEl.GetString()
                                : objEl.TryGetProperty("id", out var oIdEl) ? oIdEl.GetString() : null;
                        }

                        if (string.IsNullOrEmpty(objectUrl))
                            continue;

                        // Enqueue a fetch_badge_note job for this object URL
                        var noteJobId = await jobQueue.AddJobAsync(
                            jobType: "fetch_badge_note",
                            payload: objectUrl,
                            actorUri: actorUrl,
                            createdBy: $"sync_badge_issuer:{job.Id}",
                            notes: $"Fetch badge note from {objectUrl}");

                        totalEnqueued++;
                    }
                }

                // Move to next page
                pageUrl = pageRoot.TryGetProperty("next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String
                    ? nextEl.GetString()
                    : null;
            }

            await jobQueue.AddJobLogAsync(job.Id, $"Sync complete: enqueued {totalEnqueued} fetch_badge_note job(s)");
            await jobQueue.CompleteJobAsync(job.Id);
            _logger.LogInformation("Sync badge issuer job {JobId}: enqueued {Count} fetch jobs", job.Id, totalEnqueued);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process sync_badge_issuer job {JobId}", job.Id);
            await jobQueue.AddJobLogAsync(job.Id, $"FAILED: {ex.Message}");
            await jobQueue.FailJobAsync(job.Id, ex.Message);
        }
    }

    /// <summary>
    /// Processes a fetch_badge_note job: fetches a single object URL and runs
    /// badge extraction on it.
    /// </summary>
    private async Task ProcessFetchBadgeNoteJob(SimpleJob job, string domain, string fullDomain, JobQueueService jobQueue)
    {
        try
        {
            var objectUrl = job.Payload?.Trim('"');
            if (string.IsNullOrEmpty(objectUrl))
                throw new InvalidOperationException("fetch_badge_note job has no object URL payload");

            await jobQueue.AddJobLogAsync(job.Id, $"Fetching badge note: {objectUrl}");

            var noteJson = await FetchActivityPubObjectAsync(objectUrl);
            if (noteJson == null)
                throw new InvalidOperationException($"Failed to fetch object: {objectUrl}");

            using var noteDoc = JsonDocument.Parse(noteJson);
            var noteRoot = noteDoc.RootElement;

            // Determine the actor from attributedTo or fall back to job's actorUri
            var actor = noteRoot.TryGetProperty("attributedTo", out var attrEl)
                ? attrEl.GetString() ?? job.ActorUri
                : job.ActorUri;

            // Build a synthetic Create wrapping the fetched note
            var syntheticCreate = new InboxMessage
            {
                Type = "Create",
                Id = objectUrl,
                Actor = actor,
                Object = noteDoc.RootElement.Clone()
            };

            await ProcessBadges(syntheticCreate, job, domain, fullDomain, jobQueue);
            await jobQueue.CompleteJobAsync(job.Id);
            _logger.LogInformation("Successfully processed fetch_badge_note job {JobId}", job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process fetch_badge_note job {JobId}", job.Id);
            await jobQueue.AddJobLogAsync(job.Id, $"FAILED: {ex.Message}");
            await jobQueue.FailJobAsync(job.Id, ex.Message);
        }
    }

    /// <summary>
    /// Fetches an ActivityPub object from a URL using proper Accept headers.
    /// </summary>
    private static async Task<string?> FetchActivityPubObjectAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/activity+json", 0.9));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.8));

        var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadAsStringAsync();
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
