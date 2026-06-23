namespace FediProfile.Services;

using System.Data.SQLite;
using FediProfile.Models;

/// <summary>
/// UserScopedDb provides scoped database access for user-level operations.
/// It handles per-user databases, managing profile information for each user within a domain.
/// 
/// This database contains:
/// - User profile information: Links, Badges, Followers, InboxMessages
/// - User ActivityPub actor keys: ActorKeys
/// - Badge issuer information: BadgeIssuers, ReceivedBadges
/// - Named using both domain and userSlug (e.g., localhost_maho.db, example.com_pedro.db)
/// 
/// When instantiated in a user request context:
/// - Automatically selects the user-specific database if the user exists in the domain database
/// - Falls back to the domain database if the user doesn't exist or is not in a user context
///   (in which case domain schema is used, not user schema)
/// 
/// Scope: One instance per HTTP request, automatically selecting the appropriate user database based on context.
/// </summary>
public class UserScopedDb : LocalDbService
{
    private readonly DomainScopedDb _domainDb;
    private readonly string? _userSlug;
    private readonly string _domain;

    // ===== MISSION CATALOG =====
    // This catalog defines all available missions.
    // The database only stores completed mission codes.
    private static readonly List<UserMission> MissionCatalog = new()
    {
        new UserMission
        {
            Code = "first_link",
            Title = "First link",
            Description = "Add your first link to your profile.",
            Icon = "🔗",
            Category = "Starter",
            XpReward = 50
        },
        new UserMission
        {
            Code = "first_badge",
            Title = "First badge",
            Description = "Import your first badge.",
            Icon = "🏅",
            Category = "Starter",
            XpReward = 75
        },
        new UserMission
        {
            Code = "first_featured_badge",
            Title = "Featured badge",
            Description = "Feature your first badge on your profile.",
            Icon = "⭐",
            Category = "Starter",
            XpReward = 40
        },
        new UserMission
        {
            Code = "reach_level_3",
            Title = "Level 3",
            Description = "Reach level 3.",
            Icon = "⚡",
            Category = "Progress",
            XpReward = 50
        },
        new UserMission
        {
            Code = "reach_level_5",
            Title = "Level 5",
            Description = "Reach level 5.",
            Icon = "🚀",
            Category = "Progress",
            XpReward = 100
        },
        new UserMission
        {
            Code = "reach_level_10",
            Title = "Level 10",
            Description = "Reach level 10.",
            Icon = "👑",
            Category = "Progress",
            XpReward = 200
        },
        new UserMission
        {
            Code = "reach_level_15",
            Title = "Level 15",
            Description = "Reach level 15.",
            Icon = "💎",
            Category = "Progress",
            XpReward = 300
        },
        new UserMission
        {
            Code = "streak_1",
            Title = "Streak started",
            Description = "Start an activity streak.",
            Icon = "🔥",
            Category = "Streak",
            XpReward = 10
        },
        new UserMission
        {
            Code = "streak_3",
            Title = "3-day streak",
            Description = "Keep a 3-day streak.",
            Icon = "🔥",
            Category = "Streak",
            XpReward = 30
        },
        new UserMission
        {
            Code = "streak_5",
            Title = "5-day streak",
            Description = "Keep a 5-day streak.",
            Icon = "🔥",
            Category = "Streak",
            XpReward = 50
        },
        new UserMission
        {
            Code = "streak_10",
            Title = "10-day streak",
            Description = "Keep a 10-day streak.",
            Icon = "🔥",
            Category = "Streak",
            XpReward = 100
        },
        new UserMission
        {
            Code = "streak_25",
            Title = "25-day streak",
            Description = "Keep a 25-day streak.",
            Icon = "🔥",
            Category = "Streak",
            XpReward = 250
        },
        new UserMission
        {
            Code = "streak_50",
            Title = "50-day streak",
            Description = "Keep a 50-day streak.",
            Icon = "🔥",
            Category = "Streak",
            XpReward = 500
        },
        new UserMission
        {
            Code = "streak_100",
            Title = "100-day streak",
            Description = "Keep a 100-day streak.",
            Icon = "🏆",
            Category = "Streak",
            XpReward = 1000
        }
    };

    /// <summary>
    /// Constructor that accepts a domain and user slug.
    /// Used for manual instantiation with specific domain and user.
    /// </summary>
    public UserScopedDb(string domain, string userSlug, bool autoCreate = true)
        : base(LocalDbService.GetDbPath($"{domain}_{userSlug}.db"), null, autoCreate)
    {
        _domain = domain;
        _userSlug = userSlug;
        _domainDb = null!; // Not needed in manual mode
        if (autoCreate) EnsureCreated();
    }

    /// <summary>
    /// Constructor that automatically determines the user database from HTTP context.
    /// 
    /// Behavior:
    /// - If a user slug is present in the request context AND the user exists in the domain database:
    ///   Returns a scoped instance of the user's specific database (e.g., localhost_maho.db)
    /// - If no user slug is present OR the user doesn't exist in the domain database:
    ///   Returns a scoped instance of the domain database
    /// 
    /// This allows seamless handling of both user-scoped and domain-scoped requests.
    /// 
    /// Scope: Injected as scoped in DI, one instance per HTTP request.
    /// </summary>
    public UserScopedDb(IHttpContextAccessor httpContextAccessor, UserContextAccessor userContextAccessor, DomainScopedDb domainDb)
        : base(BuildDbPath(httpContextAccessor, userContextAccessor, domainDb))
    {
        _domain = ExtractDomain(httpContextAccessor);
        _userSlug = userContextAccessor?.GetUserSlug();
        _domainDb = domainDb;
        EnsureCreated();
    }

    /// <summary>
    /// Extracts the domain from the HTTP context.
    /// Normalizes domain name and strips port.
    /// </summary>
    private static string ExtractDomain(IHttpContextAccessor httpContextAccessor)
    {
        var domain = httpContextAccessor.HttpContext?.Request?.Host.Host ?? "localhost";
        domain = domain.Split(':')[0].Trim().ToLowerInvariant();
        return domain;
    }

    /// <summary>
    /// Builds the database path based on domain and optional user slug.
    /// When a user slug is present, first checks if the user exists in the domain database.
    /// If the user doesn't exist in the main database, returns the domain database path instead.
    /// </summary>
    private static string BuildDbPath(IHttpContextAccessor httpContextAccessor, UserContextAccessor userContextAccessor, DomainScopedDb domainDb)
    {
        var dbDataEnv = Environment.GetEnvironmentVariable("DB_DATA");
        var domain = ExtractDomain(httpContextAccessor);
        var userSlug = userContextAccessor?.GetUserSlug();

        // If no user slug present, return domain database path
        if (string.IsNullOrEmpty(userSlug))
        {
            return domainDb.DbPath;
        }

        // Check if user exists in the domain database
        var userExists = CheckUserExistsSync(domainDb, userSlug);

        if (!userExists)
        {
            // User doesn't exist in domain database, return domain database path
            return domainDb.DbPath;
        }

        // User exists, check if their database file exists
        var userDbFileName = $"{domain}_{userSlug}.db";
        var userDbPath = !string.IsNullOrEmpty(dbDataEnv)
            ? Path.Combine(dbDataEnv, userDbFileName)
            : LocalDbService.GetDbPath(userDbFileName);

        // Only return user DB path if it exists, otherwise fall back to domain DB
        if (File.Exists(userDbPath))
        {
            return userDbPath;
        }
        else
        {
            // User exists in domain DB but their database file doesn't exist yet
            // Return domain DB path (will be auto-created when needed)
            return domainDb.DbPath;
        }
    }

    /// <summary>
    /// Synchronous helper to check if a user exists in the domain database.
    /// </summary>
    private static bool CheckUserExistsSync(DomainScopedDb domainDb, string slug)
    {
        try
        {
            var task = domainDb.GetUserBySlugAsync(slug);
            task.Wait();
            return task.Result.HasValue;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the domain associated with this database.
    /// </summary>
    public string GetDomain() => _domain;

    /// <summary>
    /// Gets the user slug if in a user-scoped context, otherwise null.
    /// </summary>
    public string? GetUserSlug() => _userSlug;

    /// <summary>
    /// Determines if the current context is scoped to a specific user.
    /// </summary>
    public bool HasUserScope => !string.IsNullOrEmpty(_userSlug);

    /// <summary>
    /// Gets a reference to the associated domain database for operations that need access to all users.
    /// </summary>
    public DomainScopedDb GetDomainDatabase() => _domainDb;

    /// <summary>
    /// Overrides base initialization to create only user-level tables.
    /// User database contains: Links, ActorKeys, BadgeIssuers, ReceivedBadges, Followers, InboxMessages, Settings
    /// Reads schema from user.sql external file; falls back to inline SQL if file not found.
    /// 
    /// This is only called when initializing a true user database. If this UserScopedDb instance
    /// has fallen back to the domain database path, InitializeTables will not be called again
    /// since the file already exists with domain schema.
    /// </summary>
    protected override void InitializeTables(SQLiteConnection connection)
    {
        Console.WriteLine($"Initializing tables for UserScopedDb (Domain: {_domain}, User: {_userSlug ?? "N/A"}) {HasUserScope}");

        // Only initialize user tables if this is a true user database (has userSlug)
        if (HasUserScope && !string.IsNullOrEmpty(_userSlug))
        {
            var sqlFilePath = FindSqlFile("user.sql");
            if (sqlFilePath != null && File.Exists(sqlFilePath))
            {
                var sql = File.ReadAllText(sqlFilePath);
                Console.WriteLine($"Initializing user database with schema from {sqlFilePath}");
                foreach (var statement in SplitSqlStatements(sql))
                {
                    Console.WriteLine($"Executing SQL statement:\n{statement}\n");
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = statement;
                    cmd.ExecuteNonQuery();
                }
                return;
            } else
            {
                Console.WriteLine("user.sql schema file not found. Falling back to inline SQL for user database initialization.");
            }

            // Fallback: inline SQL if user.sql not found
            using var command = connection.CreateCommand();

            // Create Links table: User's ActivityPub deliverable links
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Links (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Icon TEXT,
                    Url TEXT NOT NULL,
                    Description TEXT,
                    AutoBoost INTEGER NOT NULL DEFAULT 0,
                    IsActivityPub INTEGER NOT NULL DEFAULT 0,
                    Category TEXT,
                    Type TEXT,
                    Following INTEGER NOT NULL DEFAULT 0,
                    Hidden INTEGER NOT NULL DEFAULT 0,
                    ActorAPUri TEXT,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();

            // Create ActorKeys table: User's public/private keypair for ActivityPub signing
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS ActorKeys (
                    Id INTEGER PRIMARY KEY,
                    PublicKeyPem TEXT NOT NULL,
                    PrivateKeyPem TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();

            // Create BadgeIssuers table: Trusted badge issuing actors
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS BadgeIssuers (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ActorUrl TEXT NOT NULL,
                    Avatar TEXT,
                    Bio TEXT,
                    Following INTEGER NOT NULL DEFAULT 0,
                    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();

            // Create ReceivedBadges table: OpenBadges received from issuers
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS ReceivedBadges (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NoteId TEXT NOT NULL UNIQUE,
                    IssuerId INTEGER NOT NULL,
                    Title TEXT NOT NULL,
                    Image TEXT,
                    Description TEXT,
                    IssuedOn TEXT,
                    AcceptedOn TEXT,
                    Hidden INTEGER NOT NULL DEFAULT 0,
                    IsFeatured INTEGER NOT NULL DEFAULT 0,
                    ReceivedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (IssuerId) REFERENCES BadgeIssuers(Id)
                );

                CREATE TABLE IF NOT EXISTS UserMissions (
                Code TEXT PRIMARY KEY,
                CompletedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();

            // Create Followers table: ActivityPub followers following this user
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Followers (
                    FollowerUri TEXT NOT NULL PRIMARY KEY,
                    Domain TEXT NOT NULL,
                    AvatarUri TEXT,
                    DisplayName TEXT,
                    Inbox TEXT NOT NULL,
                    Status INTEGER NOT NULL DEFAULT 0,
                    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();

            // Create InboxMessages table: ActivityPub activities received in user's inbox
            command.CommandText = @"
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
            ";
            command.ExecuteNonQuery();

            // Create Settings table: User-scoped profile configuration (theme, bio, avatar, etc.)
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Settings (
                    Id INTEGER PRIMARY KEY,
                    ActorUsername TEXT NOT NULL DEFAULT 'profile',
                    ActorBio TEXT,
                    ActorAvatarUrl TEXT,
                    UiTheme TEXT NOT NULL DEFAULT 'theme-classic.css',  -- see Themes.DefaultFile
                    SkipReplies INTEGER NOT NULL DEFAULT 0,
                    ShowRecentPosts INTEGER NOT NULL DEFAULT 1,
                    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UpdatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();

            // Create RecentPosts table: Rolling window of last N boosted posts for public profile
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS RecentPosts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NoteId TEXT NOT NULL UNIQUE,
                    ActorUri TEXT NOT NULL,
                    ActorName TEXT,
                    ActorAvatar TEXT,
                    Content TEXT,
                    Summary TEXT,
                    Url TEXT,
                    PublishedUtc TEXT,
                    BoostedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();

            // Create UserProgress table: Gamification progress for this profile
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS UserProgress (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    TotalXp INTEGER NOT NULL DEFAULT 0,
                    Level INTEGER NOT NULL DEFAULT 1,
                    CurrentStreakDays INTEGER NOT NULL DEFAULT 0,
                    LastActivityDate TEXT,
                    UpdatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();
        }
        // If HasUserScope is false, this instance is using domain DB path, so don't create user tables
        // The domain schema has already been created by DomainScopedDb
    }

    /// <summary>
    /// Finds a SQL file relative to the application root.
    /// </summary>
    private static string? FindSqlFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(path)) return path;

        path = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        if (File.Exists(path)) return path;

        return null;
    }

    /// <summary>
    /// Splits a SQL script into individual statements, filtering out empty/comment-only blocks.
    /// </summary>
    private static IEnumerable<string> SplitSqlStatements(string sql)
    {
        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var stmt in statements)
        {
            var trimmed = stmt.Trim();
            var lines = trimmed.Split('\n').Where(l => !l.TrimStart().StartsWith("--") && !string.IsNullOrWhiteSpace(l));
            if (lines.Any())
            {
                yield return trimmed;
            }
        }
    }

    // ===== MIGRATIONS =====

    protected override void ApplyPendingMigrations()
    {
        try
        {
            using var connection = GetConnection();
            connection.Open();

            var columns = GetTableColumns(connection, "Links");

            if (!columns.Contains("Following"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Links ADD COLUMN Following INTEGER NOT NULL DEFAULT 0;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            if (!columns.Contains("Hidden"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Links ADD COLUMN Hidden INTEGER NOT NULL DEFAULT 0;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            if (!columns.Contains("IsActivityPub"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Links ADD COLUMN IsActivityPub INTEGER NOT NULL DEFAULT 0;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            if (!columns.Contains("ActorAPUri"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Links ADD COLUMN ActorAPUri TEXT;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            if (!columns.Contains("SortOrder"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Links ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            // ReceivedBadges migrations
            var badgeColumns = GetTableColumns(connection, "ReceivedBadges");

            if (!badgeColumns.Contains("Hidden"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE ReceivedBadges ADD COLUMN Hidden INTEGER NOT NULL DEFAULT 0;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            if (!badgeColumns.Contains("IsFeatured"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE ReceivedBadges ADD COLUMN IsFeatured INTEGER NOT NULL DEFAULT 0;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            // Settings migrations
            var settingsColumns = GetTableColumns(connection, "Settings");

            if (settingsColumns.Count > 0 && !settingsColumns.Contains("SkipReplies"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Settings ADD COLUMN SkipReplies INTEGER NOT NULL DEFAULT 0;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            if (settingsColumns.Count > 0 && !settingsColumns.Contains("ShowRecentPosts"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Settings ADD COLUMN ShowRecentPosts INTEGER NOT NULL DEFAULT 1;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            // UserMissions table migration
            {
                using var createCmd = connection.CreateCommand();
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS UserMissions (
                        Code TEXT PRIMARY KEY,
                        CompletedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                ";

                try { createCmd.ExecuteNonQuery(); } catch { }
            }

            // RecentPosts table migration (create if missing)
            {
                using var createCmd = connection.CreateCommand();
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS RecentPosts (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        NoteId TEXT NOT NULL UNIQUE,
                        ActorUri TEXT NOT NULL,
                        ActorName TEXT,
                        ActorAvatar TEXT,
                        Content TEXT,
                        Summary TEXT,
                        Url TEXT,
                        PublishedUtc TEXT,
                        BoostedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                ";
                try { createCmd.ExecuteNonQuery(); } catch { }
            }

            // UserProgress table migration
            {
                using var createCmd = connection.CreateCommand();
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS UserProgress (
                        Id INTEGER PRIMARY KEY CHECK (Id = 1),
                        TotalXp INTEGER NOT NULL DEFAULT 0,
                        Level INTEGER NOT NULL DEFAULT 1,
                        CurrentStreakDays INTEGER NOT NULL DEFAULT 0,
                        LastActivityDate TEXT,
                        UpdatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                ";

                try { createCmd.ExecuteNonQuery(); } catch { }
            }

            connection.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Error applying user migrations: {ex.Message}");
        }
    }

    // ===== USER MISSIONS MANAGEMENT =====
    public async Task<List<UserMission>> GetUserMissionsAsync()
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Code, CompletedUtc FROM UserMissions";

        var completedMissions = new Dictionary<string, string?>();

        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var code = reader.GetString(0);
            var completedUtc = reader.IsDBNull(1) ? null : reader.GetString(1);

            completedMissions[code] = completedUtc;
        }

        return MissionCatalog
            .Select(mission => new UserMission
            {
                Code = mission.Code,
                Title = mission.Title,
                Description = mission.Description,
                Icon = mission.Icon,
                Category = mission.Category,
                XpReward = mission.XpReward,
                IsCompleted = completedMissions.ContainsKey(mission.Code),
                CompletedUtc = completedMissions.TryGetValue(mission.Code, out var completedUtc)
                    ? completedUtc
                    : null
            })
            .ToList();
    }

    public async Task<bool> CompleteMissionAsync(string code)
    {
        var mission = MissionCatalog.FirstOrDefault(m => m.Code == code);

        if (mission == null)
        {
            return false;
        }

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO UserMissions (Code, CompletedUtc)
            VALUES (@Code, @CompletedUtc);
        ";

        command.Parameters.AddWithValue("@Code", code);
        command.Parameters.AddWithValue("@CompletedUtc", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

        var rowsAffected = await command.ExecuteNonQueryAsync();

        if (rowsAffected <= 0)
        {
            return false;
        }

        connection.Close();

        await AddXpAsync(mission.XpReward);

        return true;
    }

    public async Task<List<UserMission>> CompleteAutomaticMissionsAsync()
    {
        var completedNow = new List<UserMission>();

        var links = await GetLinksAsync();
        var badges = await GetReceivedBadgesAsync();
        var progress = await GetUserProgressAsync();

        var missionCodes = new List<string>();

        if (links.Any())
        {
            missionCodes.Add("first_link");
        }

        if (badges.Any())
        {
            missionCodes.Add("first_badge");
        }

        if (badges.Any(b => b.IsFeatured))
        {
            missionCodes.Add("first_featured_badge");
        }

        if (progress.CurrentStreakDays >= 1)
        {
            missionCodes.Add("streak_1");
        }

        if (progress.CurrentStreakDays >= 3)
        {
            missionCodes.Add("streak_3");
        }

        if (progress.CurrentStreakDays >= 5)
        {
            missionCodes.Add("streak_5");
        }

        if (progress.CurrentStreakDays >= 10)
        {
            missionCodes.Add("streak_10");
        }

        if (progress.CurrentStreakDays >= 25)
        {
            missionCodes.Add("streak_25");
        }

        if (progress.CurrentStreakDays >= 50)
        {
            missionCodes.Add("streak_50");
        }

        if (progress.CurrentStreakDays >= 100)
        {
            missionCodes.Add("streak_100");
        }

        foreach (var code in missionCodes.Distinct())
        {
            var completed = await CompleteMissionAsync(code);

            if (completed)
            {
                var mission = MissionCatalog.FirstOrDefault(m => m.Code == code);

                if (mission != null)
                {
                    completedNow.Add(mission);
                }
            }
        }

        progress = await GetUserProgressAsync();

        var levelMissionCodes = new List<string>();

        if (progress.Level >= 3)
        {
            levelMissionCodes.Add("reach_level_3");
        }

        if (progress.Level >= 5)
        {
            levelMissionCodes.Add("reach_level_5");
        }

        if (progress.Level >= 10)
        {
            levelMissionCodes.Add("reach_level_10");
        }

        if (progress.Level >= 15)
        {
            levelMissionCodes.Add("reach_level_15");
        }

        foreach (var code in levelMissionCodes.Distinct())
        {
            var completed = await CompleteMissionAsync(code);

            if (completed)
            {
                var mission = MissionCatalog.FirstOrDefault(m => m.Code == code);

                if (mission != null)
                {
                    completedNow.Add(mission);
                }
            }
        }

        return completedNow;
    }

    // ===== LINKS MANAGEMENT =====

    public async Task<int> UpsertLinkAsync(string name, string url, string? icon = null, string? description = null, 
        bool autoBoost = false, string? category = null, string? type = null, bool hidden = false, bool isActivityPub = false,
        string? actorAPUri = null, int sortOrder = 0)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        // If sortOrder is 0, auto-assign next position
        if (sortOrder == 0)
        {
            using var maxCmd = connection.CreateCommand();
            maxCmd.CommandText = "SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM Links";
            var maxResult = await maxCmd.ExecuteScalarAsync();
            sortOrder = maxResult != null ? Convert.ToInt32(maxResult) : 1;
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Links (Name, Url, Icon, Description, AutoBoost, IsActivityPub, Category, Type, Hidden, ActorAPUri, SortOrder)
            VALUES (@Name, @Url, @Icon, @Description, @AutoBoost, @IsActivityPub, @Category, @Type, @Hidden, @ActorAPUri, @SortOrder)
            ON CONFLICT(Url) DO UPDATE SET 
                Name = @Name,
                Icon = @Icon,
                Description = @Description,
                AutoBoost = @AutoBoost,
                IsActivityPub = @IsActivityPub,
                Category = @Category,
                Type = @Type,
                Hidden = @Hidden,
                ActorAPUri = @ActorAPUri,
                SortOrder = @SortOrder;
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@Icon", icon ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Description", description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@AutoBoost", autoBoost ? 1 : 0);
        command.Parameters.AddWithValue("@IsActivityPub", isActivityPub ? 1 : 0);
        command.Parameters.AddWithValue("@Category", category ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Type", type ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Hidden", hidden ? 1 : 0);
        command.Parameters.AddWithValue("@ActorAPUri", actorAPUri ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@SortOrder", sortOrder);

        // log console.writeline the actual SQL
        Console.WriteLine($"Executing SQL:\n{command.CommandText}\nWith parameters: Name={name}, Url={url}, Icon={icon}, Description={description}, AutoBoost={autoBoost}, IsActivityPub={isActivityPub}, Category={category}, Type={type}, Hidden={hidden}");

        var result = await command.ExecuteScalarAsync();
        var linkId = result != null ? Convert.ToInt32(result) : 0;

        await RecordActivityAsync(10);
        await CompleteAutomaticMissionsAsync();

        return linkId;
    }

    public async Task<List<Link>> GetLinksAsync(bool? hidden = null)
    {
        var links = new List<Link>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        if (hidden.HasValue)
        {
            command.CommandText = "SELECT Id, Name, Icon, Url, Description, AutoBoost, IsActivityPub, Category, Type, Following, Hidden, CreatedUtc, ActorAPUri, SortOrder FROM Links WHERE Hidden = @Hidden ORDER BY SortOrder ASC, CreatedUtc DESC";
            command.Parameters.AddWithValue("@Hidden", hidden.Value ? 1 : 0);
        }
        else
        {
            command.CommandText = "SELECT Id, Name, Icon, Url, Description, AutoBoost, IsActivityPub, Category, Type, Following, Hidden, CreatedUtc, ActorAPUri, SortOrder FROM Links ORDER BY SortOrder ASC, CreatedUtc DESC";
        }

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            links.Add(new Link
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Icon = reader.IsDBNull(2) ? null : reader.GetString(2),
                Url = reader.GetString(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                AutoBoost = reader.GetInt64(5) != 0,
                IsActivityPub = reader.GetInt64(6) != 0,
                Category = reader.IsDBNull(7) ? null : reader.GetString(7),
                Type = reader.IsDBNull(8) ? null : reader.GetString(8),
                Following = reader.GetInt64(9) != 0,
                Hidden = reader.GetInt64(10) != 0,
                CreatedUtc = reader.GetString(11),
                ActorAPUri = reader.IsDBNull(12) ? null : reader.GetString(12)
            });
        }

        return links;
    }

    public async Task UpdateLinkAsync(int id, string name, string url, string? icon = null, string? description = null,
        bool autoBoost = false, string? category = null, bool hidden = false, bool isActivityPub = false,
        string? actorAPUri = null, int? sortOrder = null)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Links SET
                Name = @Name,
                Url = @Url,
                Icon = @Icon,
                Description = @Description,
                AutoBoost = @AutoBoost,
                IsActivityPub = @IsActivityPub,
                Category = @Category,
                Hidden = @Hidden,
                ActorAPUri = @ActorAPUri,
                SortOrder = CASE WHEN @SortOrder >= 0 THEN @SortOrder ELSE SortOrder END
            WHERE Id = @Id;
        ";

        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@Icon", icon ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Description", description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@AutoBoost", autoBoost ? 1 : 0);
        command.Parameters.AddWithValue("@IsActivityPub", isActivityPub ? 1 : 0);
        command.Parameters.AddWithValue("@Category", category ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Hidden", hidden ? 1 : 0);
        command.Parameters.AddWithValue("@ActorAPUri", actorAPUri ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@SortOrder", sortOrder ?? -1);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteLinkAsync(int id)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Links WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateLinkSortOrdersAsync(IEnumerable<(int Id, int SortOrder)> sortOrders)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var (id, sortOrder) in sortOrders)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Links SET SortOrder = @SortOrder WHERE Id = @Id";
                command.Parameters.AddWithValue("@SortOrder", sortOrder);
                command.Parameters.AddWithValue("@Id", id);
                await command.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<Link>> GetAutoBoostLinksAsync()
    {
        var links = new List<Link>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Icon, Url, Description, AutoBoost, IsActivityPub, Category, Type, Following, Hidden, CreatedUtc, ActorAPUri FROM Links WHERE AutoBoost = 1 ORDER BY CreatedUtc DESC";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            links.Add(new Link
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Icon = reader.IsDBNull(2) ? null : reader.GetString(2),
                Url = reader.GetString(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                AutoBoost = reader.GetInt64(5) != 0,
                IsActivityPub = reader.GetInt64(6) != 0,
                Category = reader.IsDBNull(7) ? null : reader.GetString(7),
                Type = reader.IsDBNull(8) ? null : reader.GetString(8),
                Following = reader.GetInt64(9) != 0,
                Hidden = reader.GetInt64(10) != 0,
                CreatedUtc = reader.GetString(11),
                ActorAPUri = reader.IsDBNull(12) ? null : reader.GetString(12)
            });
        }

        return links;
    }

    public async Task<bool> IsFollowingAsync(int linkId)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Following FROM Links WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", linkId);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return reader.GetInt32(0) == 1;
        }

        return false;
    }

    // ===== ACTOR KEYS MANAGEMENT =====

    public async Task<(string? PublicKey, string? PrivateKey)> GetActorKeysAsync()
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PublicKeyPem, PrivateKeyPem FROM ActorKeys WHERE Id = 1";

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (reader.GetString(0), reader.GetString(1));
        }

        return (null, null);
    }

    public async Task SetActorKeysAsync(string publicKeyPem, string privateKeyPem)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO ActorKeys (Id, PublicKeyPem, PrivateKeyPem)
            VALUES (1, @PublicKey, @PrivateKey)
            ON CONFLICT(Id) DO UPDATE SET 
                PublicKeyPem = @PublicKey,
                PrivateKeyPem = @PrivateKey;
        ";

        command.Parameters.AddWithValue("@PublicKey", publicKeyPem);
        command.Parameters.AddWithValue("@PrivateKey", privateKeyPem);

        await command.ExecuteNonQueryAsync();
    }

    // ===== BADGE ISSUERS MANAGEMENT =====

    public async Task<int> UpsertBadgeIssuerAsync(string name, string actorUrl, string? avatar = null, 
        string? bio = null, bool following = false)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO BadgeIssuers (Name, ActorUrl, Avatar, Bio, Following)
            VALUES (@Name, @ActorUrl, @Avatar, @Bio, @Following)
            ON CONFLICT(ActorUrl) DO UPDATE SET 
                Name = @Name,
                Avatar = @Avatar,
                Bio = @Bio,
                Following = @Following;
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@ActorUrl", actorUrl);
        command.Parameters.AddWithValue("@Avatar", avatar ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Bio", bio ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Following", following ? 1 : 0);

        var result = await command.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    public async Task<List<BadgeIssuer>> GetBadgeIssuersAsync(bool? following = null)
    {
        var issuers = new List<BadgeIssuer>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        if (following.HasValue)
        {
            command.CommandText = "SELECT Id, Name, ActorUrl, Avatar, Bio, Following, CreatedUtc FROM BadgeIssuers WHERE Following = @Following ORDER BY CreatedUtc DESC";
            command.Parameters.AddWithValue("@Following", following.Value ? 1 : 0);
        }
        else
        {
            command.CommandText = "SELECT Id, Name, ActorUrl, Avatar, Bio, Following, CreatedUtc FROM BadgeIssuers ORDER BY CreatedUtc DESC";
        }

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            issuers.Add(new BadgeIssuer
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                ActorUrl = reader.GetString(2),
                Avatar = reader.IsDBNull(3) ? null : reader.GetString(3),
                Bio = reader.IsDBNull(4) ? null : reader.GetString(4),
                Following = reader.GetInt64(5) != 0,
                CreatedUtc = reader.GetString(6)
            });
        }

        return issuers;
    }

    public async Task<int> CreateOrGetBadgeIssuerAsync(string name, string actorUrl, string? avatar = null)
    {
        return await UpsertBadgeIssuerAsync(name, actorUrl, avatar);
    }

    // ===== RECEIVED BADGES MANAGEMENT =====

    public async Task<int> UpsertReceivedBadgeAsync(string noteId, int issuerId, string title, string? image = null, 
        string? description = null, string? issuedOn = null, string? acceptedOn = null)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO ReceivedBadges (NoteId, IssuerId, Title, Image, Description, IssuedOn, AcceptedOn)
            VALUES (@NoteId, @IssuerId, @Title, @Image, @Description, @IssuedOn, @AcceptedOn)
            ON CONFLICT(NoteId) DO UPDATE SET 
                Title = @Title,
                Image = @Image,
                Description = @Description,
                IssuedOn = @IssuedOn,
                AcceptedOn = @AcceptedOn;
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@NoteId", noteId);
        command.Parameters.AddWithValue("@IssuerId", issuerId);
        command.Parameters.AddWithValue("@Title", title);
        command.Parameters.AddWithValue("@Image", image ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Description", description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@IssuedOn", issuedOn ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@AcceptedOn", acceptedOn ?? (object)DBNull.Value);

        var result = await command.ExecuteScalarAsync();
        var badgeId = result != null ? Convert.ToInt32(result) : 0;

        await RecordActivityAsync(25);
        await CompleteAutomaticMissionsAsync();

        return badgeId;
    }

    public async Task<List<ReceivedBadge>> GetReceivedBadgesAsync()
    {
        var badges = new List<ReceivedBadge>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, NoteId, IssuerId, Title, Image, Description, IssuedOn, AcceptedOn, Hidden, ReceivedUtc, IsFeatured FROM ReceivedBadges ORDER BY ReceivedUtc DESC";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            badges.Add(new ReceivedBadge
            {
                Id = reader.GetInt32(0),
                NoteId = reader.GetString(1),
                IssuerId = reader.GetInt32(2),
                Title = reader.GetString(3),
                Image = reader.IsDBNull(4) ? null : reader.GetString(4),
                Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                IssuedOn = reader.IsDBNull(6) ? null : reader.GetString(6),
                AcceptedOn = reader.IsDBNull(7) ? null : reader.GetString(7),
                Hidden = reader.GetInt64(8) != 0,
                ReceivedUtc = reader.GetString(9),
                IsFeatured = reader.GetInt64(10) != 0
            });
        }

        return badges;
    }

    public async Task SetBadgeHiddenAsync(int badgeId, bool hidden)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ReceivedBadges SET Hidden = @Hidden WHERE Id = @Id";
        command.Parameters.AddWithValue("@Hidden", hidden ? 1 : 0);
        command.Parameters.AddWithValue("@Id", badgeId);
        await command.ExecuteNonQueryAsync(); 
    }

    public async Task SetBadgeFeaturedAsync(int badgeId, bool isFeatured)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ReceivedBadges SET IsFeatured = @IsFeatured WHERE Id = @Id";
        command.Parameters.AddWithValue("@IsFeatured", isFeatured ? 1 : 0);
        command.Parameters.AddWithValue("@Id", badgeId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteBadgeAsync(int badgeId)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ReceivedBadges WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", badgeId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> StoreBadgeAsync(string noteId, int issuerId, string title, string? image = null, 
        string? description = null, string? issuedOn = null)
    {
        return await UpsertReceivedBadgeAsync(noteId, issuerId, title, image, description, issuedOn);
    }

    // ===== FOLLOWERS MANAGEMENT =====

    public async Task UpsertFollowerAsync(string followerUri, string domain, string? avatarUri = null, 
        string? displayName = null, string? inbox = null, int status = 0)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Followers (FollowerUri, Domain, AvatarUri, DisplayName, Inbox, Status)
            VALUES (@FollowerUri, @Domain, @AvatarUri, @DisplayName, @Inbox, @Status)
            ON CONFLICT(FollowerUri) DO UPDATE SET 
                Domain = @Domain,
                AvatarUri = @AvatarUri,
                DisplayName = @DisplayName,
                Inbox = @Inbox,
                Status = @Status;
        ";

        command.Parameters.AddWithValue("@FollowerUri", followerUri);
        command.Parameters.AddWithValue("@Domain", domain);
        command.Parameters.AddWithValue("@AvatarUri", avatarUri ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@DisplayName", displayName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Inbox", inbox ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Status", status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<Follower>> GetFollowersAsync()
    {
        var followers = new List<Follower>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FollowerUri, Domain, AvatarUri, DisplayName, Inbox, Status, CreatedUtc FROM Followers ORDER BY CreatedUtc DESC";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            followers.Add(new Follower
            {
                FollowerUri = reader.GetString(0),
                Domain = reader.GetString(1),
                AvatarUri = reader.IsDBNull(2) ? null : reader.GetString(2),
                DisplayName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Inbox = reader.GetString(4),
                Status = reader.GetInt32(5),
                CreatedUtc = reader.GetString(6)
            });
        }

        return followers;
    }

    public async Task DeleteFollowerAsync(string followerUri)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Followers WHERE FollowerUri = @FollowerUri";
        command.Parameters.AddWithValue("@FollowerUri", followerUri);

        await command.ExecuteNonQueryAsync();
    }

    public async Task RemoveFollowerAsync(string followerUri)
    {
        await DeleteFollowerAsync(followerUri);
    }

    public async Task AddFollowerAsync(string followerUri, string domain)
    {
        await UpsertFollowerAsync(followerUri, domain);
    }

    // ===== RECENT POSTS MANAGEMENT =====

    private const int MaxRecentPosts = 10;

    /// <summary>
    /// Stores a recently boosted post. If the NoteId already exists it is updated.
    /// After inserting, trims the table to keep only the last <see cref="MaxRecentPosts"/> entries.
    /// </summary>
    public async Task StoreRecentPostAsync(string noteId, string actorUri, string? actorName,
        string? actorAvatar, string? content, string? summary, string? url, string? publishedUtc)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var upsert = connection.CreateCommand();
        upsert.CommandText = @"
            INSERT INTO RecentPosts (NoteId, ActorUri, ActorName, ActorAvatar, Content, Summary, Url, PublishedUtc, BoostedUtc)
            VALUES (@NoteId, @ActorUri, @ActorName, @ActorAvatar, @Content, @Summary, @Url, @PublishedUtc, CURRENT_TIMESTAMP)
            ON CONFLICT(NoteId) DO UPDATE SET
                ActorName   = @ActorName,
                ActorAvatar = @ActorAvatar,
                Content     = @Content,
                Summary     = @Summary,
                Url         = @Url,
                PublishedUtc = @PublishedUtc,
                BoostedUtc  = CURRENT_TIMESTAMP;
        ";

        upsert.Parameters.AddWithValue("@NoteId", noteId);
        upsert.Parameters.AddWithValue("@ActorUri", actorUri);
        upsert.Parameters.AddWithValue("@ActorName", (object?)actorName ?? DBNull.Value);
        upsert.Parameters.AddWithValue("@ActorAvatar", (object?)actorAvatar ?? DBNull.Value);
        upsert.Parameters.AddWithValue("@Content", (object?)content ?? DBNull.Value);
        upsert.Parameters.AddWithValue("@Summary", (object?)summary ?? DBNull.Value);
        upsert.Parameters.AddWithValue("@Url", (object?)url ?? DBNull.Value);
        upsert.Parameters.AddWithValue("@PublishedUtc", (object?)publishedUtc ?? DBNull.Value);

        await upsert.ExecuteNonQueryAsync();

        // Trim to keep only the most recent N posts
        using var trim = connection.CreateCommand();
        trim.CommandText = @"
            DELETE FROM RecentPosts WHERE Id NOT IN (
                SELECT Id FROM RecentPosts ORDER BY BoostedUtc DESC LIMIT @Limit
            );
        ";
        trim.Parameters.AddWithValue("@Limit", MaxRecentPosts);
        await trim.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Returns the most recent boosted posts, newest first.
    /// </summary>
    public async Task<List<RecentPost>> GetRecentPostsAsync(int limit = 10)
    {
        var posts = new List<RecentPost>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, NoteId, ActorUri, ActorName, ActorAvatar, Content, Summary, Url, PublishedUtc, BoostedUtc
            FROM RecentPosts
            ORDER BY BoostedUtc DESC
            LIMIT @Limit;
        ";
        command.Parameters.AddWithValue("@Limit", limit);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            posts.Add(new RecentPost
            {
                Id = reader.GetInt32(0),
                NoteId = reader.GetString(1),
                ActorUri = reader.GetString(2),
                ActorName = reader.IsDBNull(3) ? null : reader.GetString(3),
                ActorAvatar = reader.IsDBNull(4) ? null : reader.GetString(4),
                Content = reader.IsDBNull(5) ? null : reader.GetString(5),
                Summary = reader.IsDBNull(6) ? null : reader.GetString(6),
                Url = reader.IsDBNull(7) ? null : reader.GetString(7),
                PublishedUtc = reader.IsDBNull(8) ? null : reader.GetString(8),
                BoostedUtc = reader.GetString(9)
            });
        }

        return posts;
    }

    // ===== USER PROGRESS / GAMIFICATION MANAGEMENT =====

    private const int BaseXpForNextLevel = 100;
    private const int XpIncreasePerLevel = 50;

    public static int GetXpNeededForNextLevel(int level)
    {
        level = Math.Max(1, level);
        return BaseXpForNextLevel + ((level - 1) * XpIncreasePerLevel);
    }

    public static int GetTotalXpRequiredForLevel(int level)
    {
        level = Math.Max(1, level);

        var total = 0;

        for (var currentLevel = 1; currentLevel < level; currentLevel++)
        {
            total += GetXpNeededForNextLevel(currentLevel);
        }

        return total;
    }

    public static int CalculateLevelFromXp(int totalXp)
    {
        totalXp = Math.Max(0, totalXp);

        var level = 1;

        while (totalXp >= GetTotalXpRequiredForLevel(level + 1))
        {
            level++;
        }

        return level;
    }

    public static int GetCurrentLevelXp(int totalXp, int level)
    {
        var levelStartXp = GetTotalXpRequiredForLevel(level);
        return Math.Max(0, totalXp - levelStartXp);
    }

    private static UserProgress ReadUserProgress(System.Data.Common.DbDataReader reader)
    {
        return new UserProgress
        {
            Id = reader.GetInt32(0),
            TotalXp = reader.GetInt32(1),
            Level = reader.GetInt32(2),
            CurrentStreakDays = reader.GetInt32(3),
            LastActivityDate = reader.IsDBNull(4) ? null : reader.GetString(4),
            UpdatedUtc = reader.GetString(5)
        };
    }

    private async Task EnsureUserProgressRowAsync(SQLiteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO UserProgress
                (Id, TotalXp, Level, CurrentStreakDays, LastActivityDate, UpdatedUtc)
            VALUES
                (1, 0, 1, 0, NULL, CURRENT_TIMESTAMP);
        ";

        await command.ExecuteNonQueryAsync();
    }

    public async Task<UserProgress> GetUserProgressAsync()
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        await EnsureUserProgressRowAsync(connection);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, TotalXp, Level, CurrentStreakDays, LastActivityDate, UpdatedUtc
            FROM UserProgress
            WHERE Id = 1;
        ";

        using var reader = await command.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            var progress = ReadUserProgress(reader);
            var calculatedLevel = CalculateLevelFromXp(progress.TotalXp);

            if (progress.Level != calculatedLevel)
            {
                progress.Level = calculatedLevel;

                await reader.CloseAsync();

                using var updateCommand = connection.CreateCommand();
                updateCommand.CommandText = @"
                    UPDATE UserProgress
                    SET Level = @Level,
                        UpdatedUtc = CURRENT_TIMESTAMP
                    WHERE Id = 1;
                ";
                updateCommand.Parameters.AddWithValue("@Level", progress.Level);
                await updateCommand.ExecuteNonQueryAsync();
            }

            return progress;
        }

        return new UserProgress();
    }

    public async Task<UserProgress> AddXpAsync(int amount)
    {
        if (amount <= 0)
        {
            return await GetUserProgressAsync();
        }

        using (var connection = GetConnection())
        {
            await connection.OpenAsync();

            await EnsureUserProgressRowAsync(connection);

            using var readCommand = connection.CreateCommand();
            readCommand.CommandText = @"
                SELECT TotalXp
                FROM UserProgress
                WHERE Id = 1;
            ";

            var currentTotalXp = Convert.ToInt32(await readCommand.ExecuteScalarAsync() ?? 0);
            var newTotalXp = currentTotalXp + amount;
            var newLevel = CalculateLevelFromXp(newTotalXp);

            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = @"
                UPDATE UserProgress
                SET TotalXp = @TotalXp,
                    Level = @Level,
                    UpdatedUtc = CURRENT_TIMESTAMP
                WHERE Id = 1;
            ";
            updateCommand.Parameters.AddWithValue("@TotalXp", newTotalXp);
            updateCommand.Parameters.AddWithValue("@Level", newLevel);

            await updateCommand.ExecuteNonQueryAsync();
        }

        return await GetUserProgressAsync();
    }

    public async Task<UserProgress> RecordActivityAsync(int xpAmount = 0)
    {
        using (var connection = GetConnection())
        {
            await connection.OpenAsync();

            await EnsureUserProgressRowAsync(connection);

            using var readCommand = connection.CreateCommand();
            readCommand.CommandText = @"
                SELECT TotalXp, CurrentStreakDays, LastActivityDate
                FROM UserProgress
                WHERE Id = 1;
            ";

            var totalXp = 0;
            var currentStreakDays = 0;
            string? lastActivityDate = null;

            using (var reader = await readCommand.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    totalXp = reader.GetInt32(0);
                    currentStreakDays = reader.GetInt32(1);
                    lastActivityDate = reader.IsDBNull(2) ? null : reader.GetString(2);
                }
            }

            var today = DateTime.UtcNow.Date;
            var todayText = today.ToString("yyyy-MM-dd");

            var newStreakDays = currentStreakDays;

            if (string.IsNullOrWhiteSpace(lastActivityDate))
            {
                newStreakDays = 1;
            }
            else if (DateTime.TryParse(lastActivityDate, out var lastDate))
            {
                lastDate = lastDate.Date;

                if (lastDate == today)
                {
                    newStreakDays = currentStreakDays;
                }
                else if (lastDate == today.AddDays(-1))
                {
                    newStreakDays = currentStreakDays + 1;
                }
                else
                {
                    newStreakDays = 1;
                }
            }
            else
            {
                newStreakDays = 1;
            }

            var newTotalXp = totalXp + Math.Max(0, xpAmount);
            var newLevel = CalculateLevelFromXp(newTotalXp);

            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = @"
                UPDATE UserProgress
                SET TotalXp = @TotalXp,
                    Level = @Level,
                    CurrentStreakDays = @CurrentStreakDays,
                    LastActivityDate = @LastActivityDate,
                    UpdatedUtc = CURRENT_TIMESTAMP
                WHERE Id = 1;
            ";

            updateCommand.Parameters.AddWithValue("@TotalXp", newTotalXp);
            updateCommand.Parameters.AddWithValue("@Level", newLevel);
            updateCommand.Parameters.AddWithValue("@CurrentStreakDays", newStreakDays);
            updateCommand.Parameters.AddWithValue("@LastActivityDate", todayText);

            await updateCommand.ExecuteNonQueryAsync();
        }

        return await GetUserProgressAsync();
    }

    // ===== USER SETTINGS MANAGEMENT =====

    public async Task UpsertSettingsAsync(string? actorUsername = null, string? actorBio = null, 
        string? actorAvatarUrl = null, string? uiTheme = null, bool? skipReplies = null, bool? showRecentPosts = null)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        // Build the SET clauses dynamically — only update fields the caller explicitly provided
        var setClauses = new List<string>();
        if (actorUsername != null) setClauses.Add("ActorUsername = @ActorUsername");
        if (actorBio != null) setClauses.Add("ActorBio = @ActorBio");
        if (actorAvatarUrl != null) setClauses.Add("ActorAvatarUrl = @ActorAvatarUrl");
        if (uiTheme != null) setClauses.Add("UiTheme = @UiTheme");
        if (skipReplies != null) setClauses.Add("SkipReplies = @SkipReplies");
        if (showRecentPosts != null) setClauses.Add("ShowRecentPosts = @ShowRecentPosts");
        setClauses.Add("UpdatedUtc = CURRENT_TIMESTAMP");

        var updateSet = string.Join(",\n                ", setClauses);

        using var command = connection.CreateCommand();
        command.CommandText = $@"
            INSERT INTO Settings (Id, ActorUsername, ActorBio, ActorAvatarUrl, UiTheme, SkipReplies, ShowRecentPosts, CreatedUtc, UpdatedUtc)
            VALUES (1, @InsertActorUsername, @InsertActorBio, @InsertActorAvatarUrl, @InsertUiTheme, @InsertSkipReplies, @InsertShowRecentPosts, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                {updateSet};
        ";

        // INSERT defaults: use provided value or fall back to sensible defaults
        command.Parameters.AddWithValue("@InsertActorUsername", actorUsername ?? "username");
        command.Parameters.AddWithValue("@InsertActorBio", (object?)actorBio ?? DBNull.Value);
        command.Parameters.AddWithValue("@InsertActorAvatarUrl", (object?)actorAvatarUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@InsertUiTheme", uiTheme ?? "theme-classic.css");
        command.Parameters.AddWithValue("@InsertSkipReplies", skipReplies == true ? 1 : 0);
        command.Parameters.AddWithValue("@InsertShowRecentPosts", showRecentPosts != false ? 1 : 0);

        // UPDATE params: only added when the caller provided a non-null value
        if (actorUsername != null) command.Parameters.AddWithValue("@ActorUsername", actorUsername);
        if (actorBio != null) command.Parameters.AddWithValue("@ActorBio", actorBio);
        if (actorAvatarUrl != null) command.Parameters.AddWithValue("@ActorAvatarUrl", actorAvatarUrl);
        if (uiTheme != null) command.Parameters.AddWithValue("@UiTheme", uiTheme);
        if (skipReplies != null) command.Parameters.AddWithValue("@SkipReplies", skipReplies.Value ? 1 : 0);
        if (showRecentPosts != null) command.Parameters.AddWithValue("@ShowRecentPosts", showRecentPosts.Value ? 1 : 0);

        await command.ExecuteNonQueryAsync();
    }
}
