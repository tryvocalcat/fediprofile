-- FediProfile User Database Schema
-- This schema is for per-user databases (e.g., localhost_maho.db, example.com_alice.db)
-- It contains user profile information, badges, links, followers, etc.

-- ==============================================================================
-- Links table: User's profile links displayed in their profile

CREATE TABLE IF NOT EXISTS Links (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Icon TEXT,
    Url TEXT NOT NULL UNIQUE,
    Description TEXT,
    AutoBoost INTEGER NOT NULL DEFAULT 0,
    IsActivityPub INTEGER NOT NULL DEFAULT 0,
    Category TEXT,
    Type TEXT,
    Following INTEGER NOT NULL DEFAULT 0,
    Hidden INTEGER NOT NULL DEFAULT 0,
    ActorAPUri TEXT,
    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);


-- ==============================================================================
-- ActorKeys table: User's public/private keypair for ActivityPub signing
-- ==============================================================================
CREATE TABLE IF NOT EXISTS ActorKeys (
    Id INTEGER PRIMARY KEY,
    PublicKeyPem TEXT NOT NULL,
    PrivateKeyPem TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ==============================================================================
-- BadgeIssuers table: Trusted ActivityPub actors that can issue badges
-- ==============================================================================
CREATE TABLE IF NOT EXISTS BadgeIssuers (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    ActorUrl TEXT NOT NULL,
    Avatar TEXT,
    Bio TEXT,
    Following INTEGER NOT NULL DEFAULT 0,
    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ==============================================================================
-- ReceivedBadges table: OpenBadges received from BadgeIssuers
-- ==============================================================================
CREATE TABLE IF NOT EXISTS ReceivedBadges (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    NoteId TEXT NOT NULL UNIQUE,
    IssuerId INTEGER NOT NULL,
    Title TEXT NOT NULL,
    Image TEXT,
    Description TEXT,
    IssuedOn TEXT,
    AcceptedOn TEXT,
    ReceivedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (IssuerId) REFERENCES BadgeIssuers(Id)
);

-- ==============================================================================
-- Followers table: ActivityPub followers following this user
-- ==============================================================================
CREATE TABLE IF NOT EXISTS Followers (
    FollowerUri TEXT NOT NULL PRIMARY KEY,
    Domain TEXT NOT NULL,
    AvatarUri TEXT,
    DisplayName TEXT,
    Inbox TEXT NOT NULL,
    Status INTEGER NOT NULL DEFAULT 0,
    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ==============================================================================
-- InboxMessages table: ActivityPub activities received in the inbox
-- ==============================================================================
CREATE TABLE IF NOT EXISTS InboxMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ActivityId TEXT NOT NULL UNIQUE,
    ActivityType TEXT NOT NULL,
    ActorUri TEXT NOT NULL,
    Content TEXT NOT NULL,
    Processed INTEGER NOT NULL DEFAULT 0,
    ProcessedAt TEXT,
    ReceivedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ==============================================================================
-- Settings table: User-scoped profile configuration
-- ==============================================================================
CREATE TABLE IF NOT EXISTS Settings (
    Id INTEGER PRIMARY KEY,
    ActorUsername TEXT NOT NULL DEFAULT 'profile',
    ActorBio TEXT,
    ActorAvatarUrl TEXT,
    UiTheme TEXT NOT NULL DEFAULT 'theme-classic.css',
    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
