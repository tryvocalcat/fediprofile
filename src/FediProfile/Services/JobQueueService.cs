using System.Data.SQLite;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FediProfile.Services;

public class SimpleJob
{
    public long Id { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? Payload { get; set; }
    public string? ActorUri { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int CurrentRetry { get; set; } = 0;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ScheduledFor { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Service for managing the job queue stored in the domain-level SQLite database.
/// Provides methods to enqueue, dequeue, complete, fail, and log jobs.
/// </summary>
public class JobQueueService
{
    private readonly ILogger<JobQueueService>? _logger;
    private readonly DomainScopedDb _domainDb;

    public JobQueueService(DomainScopedDb domainDb, ILogger<JobQueueService>? logger = null)
    {
        _domainDb = domainDb;
        _logger = logger;
    }

    /// <summary>
    /// Add a job to the queue.
    /// </summary>
    public async Task<long> AddJobAsync(string jobType, object? payload = null, string? actorUri = null,
        DateTime? scheduledFor = null, string? createdBy = null, string? notes = null)
    {
        using var connection = _domainDb.GetConnection();
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Jobs (JobType, Payload, ActorUri, ScheduledFor, CreatedBy, Notes)
            VALUES (@jobType, @payload, @actorUri, @scheduledFor, @createdBy, @notes);
            SELECT last_insert_rowid();";

        command.Parameters.AddWithValue("@jobType", jobType);
        // If payload is already a string (raw JSON), store it directly; otherwise serialize it
        var payloadValue = payload switch
        {
            null => (object)DBNull.Value,
            string s => s,
            _ => JsonSerializer.Serialize(payload)
        };
        command.Parameters.AddWithValue("@payload", payloadValue);
        command.Parameters.AddWithValue("@actorUri", actorUri ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@scheduledFor", scheduledFor ?? DateTime.UtcNow);
        command.Parameters.AddWithValue("@createdBy", createdBy ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@notes", notes ?? (object)DBNull.Value);

        var jobId = (long)command.ExecuteScalar();

        _logger?.LogInformation("Added job {JobId} of type {JobType}, actor {ActorUri}", jobId, jobType, actorUri);

        return jobId;
    }

    /// <summary>
    /// Get the next pending job and atomically mark it as processing.
    /// </summary>
    public async Task<SimpleJob?> GetNextJobAsync()
    {
        using var connection = _domainDb.GetConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();
        try
        {
            var selectCommand = connection.CreateCommand();
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = @"
                SELECT Id, JobType, Status, Payload, ActorUri, MaxRetries, CurrentRetry,
                       LastError, CreatedAt, ScheduledFor, StartedAt, CompletedAt,
                       ProcessedAt, CreatedBy, Notes
                FROM Jobs
                WHERE Status = 'pending' AND ScheduledFor <= @now
                ORDER BY Priority DESC, ScheduledFor ASC
                LIMIT 1";
            selectCommand.Parameters.AddWithValue("@now", DateTime.UtcNow);

            using var reader = selectCommand.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var job = new SimpleJob
            {
                Id = reader.GetInt64(0),
                JobType = reader.GetString(1),
                Status = reader.GetString(2),
                Payload = reader.IsDBNull(3) ? null : reader.GetString(3),
                ActorUri = reader.IsDBNull(4) ? null : reader.GetString(4),
                MaxRetries = reader.GetInt32(5),
                CurrentRetry = reader.GetInt32(6),
                LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetDateTime(8),
                ScheduledFor = reader.GetDateTime(9),
                StartedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                CompletedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                ProcessedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                CreatedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
                Notes = reader.IsDBNull(14) ? null : reader.GetString(14)
            };
            reader.Close();

            // Atomically mark as processing
            var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = @"
                UPDATE Jobs
                SET Status = 'processing', StartedAt = @startedAt
                WHERE Id = @id AND Status = 'pending'";
            updateCommand.Parameters.AddWithValue("@id", job.Id);
            updateCommand.Parameters.AddWithValue("@startedAt", DateTime.UtcNow);

            var rowsAffected = updateCommand.ExecuteNonQuery();
            if (rowsAffected == 0)
            {
                transaction.Rollback();
                return null; // Job was taken by another worker
            }

            transaction.Commit();
            job.Status = "processing";
            job.StartedAt = DateTime.UtcNow;

            _logger?.LogInformation("Dequeued job {JobId} of type {JobType}", job.Id, job.JobType);
            return job;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger?.LogError(ex, "Error getting next job");
            throw;
        }
    }

    /// <summary>
    /// Mark job as completed.
    /// </summary>
    public async Task CompleteJobAsync(long jobId)
    {
        await UpdateJobStatusAsync(jobId, "completed");
        _logger?.LogInformation("Completed job {JobId}", jobId);
    }

    /// <summary>
    /// Mark job as failed and handle retry logic with exponential backoff.
    /// </summary>
    public async Task FailJobAsync(long jobId, string? error = null, bool canRetry = true)
    {
        using var connection = _domainDb.GetConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();
        try
        {
            var selectCommand = connection.CreateCommand();
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = "SELECT CurrentRetry, MaxRetries FROM Jobs WHERE Id = @id";
            selectCommand.Parameters.AddWithValue("@id", jobId);

            using var reader = selectCommand.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Job {jobId} not found");
            }

            var currentRetry = reader.GetInt32(0);
            var maxRetries = reader.GetInt32(1);
            reader.Close();

            if (canRetry && currentRetry < maxRetries)
            {
                // Schedule for retry with exponential backoff
                var retryDelay = TimeSpan.FromMinutes(Math.Pow(2, currentRetry));
                var scheduledFor = DateTime.UtcNow.Add(retryDelay);

                var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = @"
                    UPDATE Jobs
                    SET Status = 'pending', CurrentRetry = CurrentRetry + 1, LastError = @error,
                        ScheduledFor = @scheduledFor, StartedAt = NULL
                    WHERE Id = @id";

                updateCommand.Parameters.AddWithValue("@id", jobId);
                updateCommand.Parameters.AddWithValue("@error", error ?? (object)DBNull.Value);
                updateCommand.Parameters.AddWithValue("@scheduledFor", scheduledFor);
                updateCommand.ExecuteNonQuery();

                await AddJobLogAsync(jobId, $"RETRY {currentRetry + 1}/{maxRetries}: {error}", connection, transaction);

                _logger?.LogWarning("Job {JobId} failed, scheduled for retry {Retry}/{MaxRetries} at {ScheduledFor}: {Error}",
                    jobId, currentRetry + 1, maxRetries, scheduledFor, error);
            }
            else
            {
                // Mark as permanently failed
                var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = @"
                    UPDATE Jobs
                    SET Status = 'failed', LastError = @error, CompletedAt = @completedAt, ProcessedAt = @processedAt
                    WHERE Id = @id";

                updateCommand.Parameters.AddWithValue("@id", jobId);
                updateCommand.Parameters.AddWithValue("@error", error ?? (object)DBNull.Value);
                updateCommand.Parameters.AddWithValue("@completedAt", DateTime.UtcNow);
                updateCommand.Parameters.AddWithValue("@processedAt", DateTime.UtcNow);
                updateCommand.ExecuteNonQuery();

                await AddJobLogAsync(jobId, $"FAILED PERMANENTLY after {currentRetry} retries: {error}", connection, transaction);

                _logger?.LogError("Job {JobId} permanently failed after {Retry} retries: {Error}",
                    jobId, currentRetry, error);
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger?.LogError(ex, "Error failing job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Add a log entry for a job to the JobLogs table.
    /// </summary>
    public async Task AddJobLogAsync(long jobId, string message, SQLiteConnection? connection = null, SQLiteTransaction? transaction = null)
    {
        var shouldDisposeConnection = connection == null;
        if (connection == null)
        {
            connection = _domainDb.GetConnection();
            connection.Open();
        }

        try
        {
            var command = connection.CreateCommand();
            if (transaction != null)
                command.Transaction = transaction;

            command.CommandText = @"
                INSERT INTO JobLogs (JobId, Message)
                VALUES (@jobId, @message)";

            command.Parameters.AddWithValue("@jobId", jobId);
            command.Parameters.AddWithValue("@message", message);

            command.ExecuteNonQuery();
        }
        finally
        {
            if (shouldDisposeConnection)
                connection.Dispose();
        }
    }

    /// <summary>
    /// Check if a pending or processing job already exists for this job type and actor.
    /// </summary>
    public bool HasPendingJob(string jobType, string actorUri)
    {
        using var connection = _domainDb.GetConnection();
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*) FROM Jobs
            WHERE JobType = @jobType
              AND ActorUri = @actorUri
              AND Status IN ('pending', 'processing')";

        command.Parameters.AddWithValue("@jobType", jobType);
        command.Parameters.AddWithValue("@actorUri", actorUri);

        var count = (long)command.ExecuteScalar();
        return count > 0;
    }

    private async Task UpdateJobStatusAsync(long jobId, string status)
    {
        using var connection = _domainDb.GetConnection();
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Jobs
            SET Status = @status, CompletedAt = @completedAt, ProcessedAt = @processedAt
            WHERE Id = @id";

        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@status", status);
        var now = DateTime.UtcNow;
        command.Parameters.AddWithValue("@completedAt", status == "completed" || status == "failed" ? now : DBNull.Value);
        command.Parameters.AddWithValue("@processedAt", status == "completed" || status == "failed" ? now : DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Get all jobs ordered by CreatedAt descending, with optional limit.
    /// </summary>
    public List<SimpleJob> GetAllJobs(int limit = 100)
    {
        var jobs = new List<SimpleJob>();
        using var connection = _domainDb.GetConnection();
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, JobType, Status, Payload, ActorUri, MaxRetries, CurrentRetry,
                   LastError, CreatedAt, ScheduledFor, StartedAt, CompletedAt,
                   ProcessedAt, CreatedBy, Notes
            FROM Jobs
            ORDER BY CreatedAt DESC
            LIMIT @limit";
        command.Parameters.AddWithValue("@limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    /// <summary>
    /// Get a single job by ID.
    /// </summary>
    public SimpleJob? GetJobById(long jobId)
    {
        using var connection = _domainDb.GetConnection();
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, JobType, Status, Payload, ActorUri, MaxRetries, CurrentRetry,
                   LastError, CreatedAt, ScheduledFor, StartedAt, CompletedAt,
                   ProcessedAt, CreatedBy, Notes
            FROM Jobs
            WHERE Id = @id";
        command.Parameters.AddWithValue("@id", jobId);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return ReadJob(reader);
        }

        return null;
    }

    /// <summary>
    /// Get all log entries for a given job, ordered by CreatedAt ascending.
    /// </summary>
    public List<SimpleJobLog> GetJobLogs(long jobId)
    {
        var logs = new List<SimpleJobLog>();
        using var connection = _domainDb.GetConnection();
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, JobId, Message, CreatedAt
            FROM JobLogs
            WHERE JobId = @jobId
            ORDER BY CreatedAt ASC";
        command.Parameters.AddWithValue("@jobId", jobId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            logs.Add(new SimpleJobLog
            {
                Id = reader.GetInt64(0),
                JobId = reader.GetInt64(1),
                Message = reader.GetString(2),
                CreatedAt = reader.GetDateTime(3)
            });
        }

        return logs;
    }

    /// <summary>
    /// Reset a failed job back to pending for a new attempt.
    /// </summary>
    public async Task RetryJobAsync(long jobId)
    {
        using var connection = _domainDb.GetConnection();
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Jobs
            SET Status = 'pending', ScheduledFor = @now, StartedAt = NULL,
                CompletedAt = NULL, ProcessedAt = NULL
            WHERE Id = @id AND Status = 'failed'";
        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow);

        var rows = command.ExecuteNonQuery();
        if (rows > 0)
        {
            await AddJobLogAsync(jobId, "Manually retried by admin");
            _logger?.LogInformation("Job {JobId} manually retried", jobId);
        }
    }

    private static SimpleJob ReadJob(SQLiteDataReader reader)
    {
        return new SimpleJob
        {
            Id = reader.GetInt64(0),
            JobType = reader.GetString(1),
            Status = reader.GetString(2),
            Payload = reader.IsDBNull(3) ? null : reader.GetString(3),
            ActorUri = reader.IsDBNull(4) ? null : reader.GetString(4),
            MaxRetries = reader.GetInt32(5),
            CurrentRetry = reader.GetInt32(6),
            LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = reader.GetDateTime(8),
            ScheduledFor = reader.GetDateTime(9),
            StartedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            CompletedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            ProcessedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
            CreatedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
            Notes = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }
}

public class SimpleJobLog
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
