namespace FediProfile.Services;

using System.Data.SQLite;

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
                    Category TEXT,
                    Type TEXT,
                    Following INTEGER NOT NULL DEFAULT 0,
                    Hidden INTEGER NOT NULL DEFAULT 0,
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
                    ReceivedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (IssuerId) REFERENCES BadgeIssuers(Id)
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
                    UiTheme TEXT NOT NULL DEFAULT 'theme-classic.css',
                    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
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

            connection.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Error applying user migrations: {ex.Message}");
        }
    }

    // ===== LINKS MANAGEMENT =====

    public async Task<int> UpsertLinkAsync(string name, string url, string? icon = null, string? description = null, 
        bool autoBoost = false, string? category = null, string? type = null, bool hidden = false)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Links (Name, Url, Icon, Description, AutoBoost, Category, Type, Hidden)
            VALUES (@Name, @Url, @Icon, @Description, @AutoBoost, @Category, @Type, @Hidden)
            ON CONFLICT(Url) DO UPDATE SET 
                Name = @Name,
                Icon = @Icon,
                Description = @Description,
                AutoBoost = @AutoBoost,
                Category = @Category,
                Type = @Type,
                Hidden = @Hidden;
            SELECT last_insert_rowid();
        ";

        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@Icon", icon ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Description", description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@AutoBoost", autoBoost ? 1 : 0);
        command.Parameters.AddWithValue("@Category", category ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Type", type ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Hidden", hidden ? 1 : 0);

        // log console.writeline the actual SQL
        Console.WriteLine($"Executing SQL:\n{command.CommandText}\nWith parameters: Name={name}, Url={url}, Icon={icon}, Description={description}, AutoBoost={autoBoost}, Category={category}, Type={type}, Hidden={hidden}");

        var result = await command.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    public async Task<List<dynamic>> GetLinksAsync(bool? hidden = null)
    {
        var links = new List<dynamic>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        if (hidden.HasValue)
        {
            command.CommandText = "SELECT * FROM Links WHERE Hidden = @Hidden ORDER BY CreatedUtc DESC";
            command.Parameters.AddWithValue("@Hidden", hidden.Value ? 1 : 0);
        }
        else
        {
            command.CommandText = "SELECT * FROM Links ORDER BY CreatedUtc DESC";
        }

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var link = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                link[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            links.Add(link);
        }

        return links;
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

    public async Task<List<dynamic>> GetAutoBoostLinksAsync()
    {
        var links = new List<dynamic>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Links WHERE AutoBoost = 1 ORDER BY CreatedUtc DESC";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var link = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                link[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            links.Add(link);
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

    public async Task<List<dynamic>> GetBadgeIssuersAsync(bool? following = null)
    {
        var issuers = new List<dynamic>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        if (following.HasValue)
        {
            command.CommandText = "SELECT * FROM BadgeIssuers WHERE Following = @Following ORDER BY CreatedUtc DESC";
            command.Parameters.AddWithValue("@Following", following.Value ? 1 : 0);
        }
        else
        {
            command.CommandText = "SELECT * FROM BadgeIssuers ORDER BY CreatedUtc DESC";
        }

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var issuer = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                issuer[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            issuers.Add(issuer);
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
        return result != null ? Convert.ToInt32(result) : 0;
    }

    public async Task<List<dynamic>> GetReceivedBadgesAsync()
    {
        var badges = new List<dynamic>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ReceivedBadges ORDER BY ReceivedUtc DESC";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var badge = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                badge[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            badges.Add(badge);
        }

        return badges;
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

    public async Task<List<dynamic>> GetFollowersAsync()
    {
        var followers = new List<dynamic>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Followers ORDER BY CreatedUtc DESC";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var follower = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                follower[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            followers.Add(follower);
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

    // ===== USER SETTINGS MANAGEMENT =====

    public async Task UpsertSettingsAsync(string? actorUsername = null, string? actorBio = null, 
        string? actorAvatarUrl = null, string? uiTheme = null)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Settings (Id, ActorUsername, ActorBio, ActorAvatarUrl, UiTheme, CreatedUtc, UpdatedUtc)
            VALUES (1, @ActorUsername, @ActorBio, @ActorAvatarUrl, @UiTheme, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT(Id) DO UPDATE SET
                ActorUsername = COALESCE(@ActorUsername, ActorUsername),
                ActorBio = COALESCE(@ActorBio, ActorBio),
                ActorAvatarUrl = COALESCE(@ActorAvatarUrl, ActorAvatarUrl),
                UiTheme = COALESCE(@UiTheme, UiTheme),
                UpdatedUtc = CURRENT_TIMESTAMP;
        ";

        command.Parameters.AddWithValue("@ActorUsername", (object?)actorUsername ?? DBNull.Value);
        command.Parameters.AddWithValue("@ActorBio", (object?)actorBio ?? DBNull.Value);
        command.Parameters.AddWithValue("@ActorAvatarUrl", (object?)actorAvatarUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@UiTheme", (object?)uiTheme ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }
}
