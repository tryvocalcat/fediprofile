-- FediProfile Domain Database Schema
-- This schema is for the domain-level database (e.g., localhost.db, example.com.db)
-- It contains user accounts for this domain with their Mastodon OAuth credentials.

-- ==============================================================================
-- Users table: Stores all user accounts (userSlug) for this domain
-- ==============================================================================
CREATE TABLE IF NOT EXISTS Users (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Slug TEXT NOT NULL UNIQUE COLLATE NOCASE,
    DisplayName TEXT,
    MastodonUser TEXT NOT NULL,
    MastodonServer TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    DeletedUtc TEXT
);

CREATE INDEX IF NOT EXISTS idx_users_slug ON Users(Slug);

-- ==============================================================================
-- Following table: Domain-level index of which local users follow which actors
-- Used by the shared inbox to fan-out incoming activities without scanning
-- per-user databases. Written when a user enables AutoBoost on a link.
-- ==============================================================================
CREATE TABLE IF NOT EXISTS Following (
    UserSlug TEXT NOT NULL COLLATE NOCASE,
    ActorUrl TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (UserSlug, ActorUrl)
);

CREATE INDEX IF NOT EXISTS IX_Following_ActorUrl ON Following (ActorUrl);

-- ==============================================================================
-- Jobs table: Simple generic job queue for background processing
-- ==============================================================================
CREATE TABLE IF NOT EXISTS Jobs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    JobType TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'pending', -- pending, processing, completed, failed
    Payload TEXT NULL, -- JSON data for the job (serialized InboxMessage)
    
    ActorUri TEXT NULL, -- The remote actor URI that originated the activity
    
    -- Retry and error handling
    MaxRetries INTEGER NOT NULL DEFAULT 3,
    CurrentRetry INTEGER NOT NULL DEFAULT 0,
    LastError TEXT NULL,

    Priority INTEGER NOT NULL DEFAULT 0, -- Higher number = higher priority
    
    -- Timestamps
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ScheduledFor DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, -- When job should be executed
    StartedAt DATETIME NULL, -- When job processing began
    CompletedAt DATETIME NULL, -- When job finished (success or failure)
    ProcessedAt DATETIME NULL,
    
    -- Additional metadata
    CreatedBy TEXT NULL, -- Source that created the job (controller, service, etc.)
    Notes TEXT NULL -- Additional context or metadata
);

CREATE INDEX IF NOT EXISTS IX_Jobs_Status_Created ON Jobs(Status, CreatedAt);
CREATE INDEX IF NOT EXISTS IX_Jobs_Status_Scheduled ON Jobs(Status, ScheduledFor);
CREATE INDEX IF NOT EXISTS IX_Jobs_ActorUri ON Jobs(ActorUri);
CREATE INDEX IF NOT EXISTS IX_Jobs_Type_Actor ON Jobs(JobType, ActorUri);

-- ==============================================================================
-- JobLogs table: Job logs for debugging
-- ==============================================================================
CREATE TABLE IF NOT EXISTS JobLogs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    JobId INTEGER NOT NULL,
    Message TEXT NOT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id)
);

CREATE INDEX IF NOT EXISTS IX_JobLogs_JobId ON JobLogs(JobId);

-- ==============================================================================
-- VerifiedUris table: URIs that a user has verified belong to them
-- ==============================================================================
CREATE TABLE IF NOT EXISTS VerifiedUris (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserSlug TEXT NOT NULL COLLATE NOCASE,
    Uri TEXT NOT NULL,
    VerifiedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (UserSlug, Uri)
);

CREATE INDEX IF NOT EXISTS IX_VerifiedUris_UserSlug ON VerifiedUris(UserSlug);

-- ==============================================================================
-- BadgeIssuers table: Trusted ActivityPub actors that can issue badges to users
-- Scoped per user via UserSlug. The Id is referenced by ReceivedBadges in user DBs.
-- ==============================================================================
CREATE TABLE IF NOT EXISTS BadgeIssuers (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    ActorUrl TEXT NOT NULL,
    Avatar TEXT,
    Bio TEXT,
    Domain TEXT,
    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (ActorUrl)
);