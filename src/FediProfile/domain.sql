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