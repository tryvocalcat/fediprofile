namespace FediProfile.Services;

using System.Data.SQLite;

/// <summary>
/// DomainScopedDb provides scoped database access for domain-level operations.
/// It handles multi-tenant support with one database per domain (e.g., localhost.db, example.com.db).
/// 
/// This database contains:
/// - Users table: All user accounts (userSlug, displayName) for this domain
/// - Settings table: Instance-level configuration (not per-user)
/// 
/// Scope: One instance per HTTP request, automatically selecting the domain database.
/// </summary>
public class DomainScopedDb : LocalDbService
{
    private readonly string _domain;

    /// <summary>
    /// Constructor that accepts a domain name.
    /// Used for manual instantiation with a specific domain.
    /// </summary>
    public DomainScopedDb(string domain, bool autoCreate = true) 
        : base(LocalDbService.GetDbPath($"{domain}.db"), null, autoCreate)
    {
        _domain = domain;
        if (autoCreate) EnsureCreated();
    }

    /// <summary>
    /// Constructor that automatically determines the domain from the HTTP context.
    /// Extracts the domain from the request host and creates/uses the domain-specific database.
    /// Falls back to localhost if no HTTP context is available.
    /// 
    /// Scope: Injected as scoped in DI, one instance per HTTP request.
    /// </summary>
    public DomainScopedDb(IHttpContextAccessor httpContextAccessor)
        : base(BuildDbPath(httpContextAccessor))
    {
        _domain = ExtractDomain(httpContextAccessor);
        EnsureCreated();
    }

    /// <summary>
    /// Extracts the domain from the HTTP context.
    /// Normalizes domain name and strips port.
    /// </summary>
    private static string ExtractDomain(IHttpContextAccessor httpContextAccessor)
    {
        var domain = httpContextAccessor.HttpContext?.Request?.Host.Host ?? "localhost";
        // Strip port if present (host should already be without port, but handle edge cases)
        domain = domain.Split(':')[0].Trim().ToLowerInvariant();
        return domain;
    }

    /// <summary>
    /// Builds the database path for the domain.
    /// Format: {domain}.db (e.g., localhost.db, example.com.db)
    /// </summary>
    private static string BuildDbPath(IHttpContextAccessor httpContextAccessor)
    {
        var domain = ExtractDomain(httpContextAccessor);
        var dbDataEnv = Environment.GetEnvironmentVariable("DB_DATA");
        var dbFileName = $"{domain}.db";

        if (!string.IsNullOrEmpty(dbDataEnv))
        {
            return Path.Combine(dbDataEnv, dbFileName);
        }

        return LocalDbService.GetDbPath(dbFileName);
    }

    /// <summary>
    /// Gets the domain associated with this database.
    /// </summary>
    public string GetDomain() => _domain;

    /// <summary>
    /// Overrides base initialization to create only domain-level tables.
    /// Domain database contains: Users with OAuth credentials, Settings
    /// Reads schema from domain.sql external file; falls back to inline SQL if file not found.
    /// </summary>
    protected override void InitializeTables(SQLiteConnection connection)
    {
        var sqlFilePath = FindSqlFile("domain.sql");
        if (sqlFilePath != null && File.Exists(sqlFilePath))
        {
            var sql = File.ReadAllText(sqlFilePath);
            // Split on semicolons to execute individual statements, skipping comments-only blocks
            foreach (var statement in SplitSqlStatements(sql))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = statement;
                cmd.ExecuteNonQuery();
            }
            return;
        }

        // Fallback: inline SQL if domain.sql not found
        using var command = connection.CreateCommand();

        // Create Users table: Domain-level user accounts with Mastodon OAuth info
        command.CommandText = @"
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
        ";
        command.ExecuteNonQuery();

        // Create Settings table: Domain-level configuration (admin credentials, etc.)
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Settings (
                Id INTEGER PRIMARY KEY,
                ActorUsername TEXT NOT NULL DEFAULT 'profile',
                ActorBio TEXT,
                ActorAvatarUrl TEXT,
                UiTheme TEXT NOT NULL DEFAULT 'theme-classic.css',
                AdminMastodonUser TEXT,
                AdminMastodonDomain TEXT,
                CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UpdatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
        ";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Finds a SQL file relative to the application root.
    /// </summary>
    private static string? FindSqlFile(string fileName)
    {
        // Check relative to current directory
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(path)) return path;

        // Check in current working directory
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
            // Skip empty or comment-only statements
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

            var settingsColumns = GetTableColumns(connection, "Settings");

            if (!settingsColumns.Contains("AdminMastodonUser"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Settings ADD COLUMN AdminMastodonUser TEXT;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            if (!settingsColumns.Contains("AdminMastodonDomain"))
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Settings ADD COLUMN AdminMastodonDomain TEXT;";
                try { alterCommand.ExecuteNonQuery(); } catch { }
            }

            // Ensure the Following index table exists
            EnsureFollowingTable();

            connection.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Error applying domain migrations: {ex.Message}");
        }
    }

    // ===== USER MANAGEMENT METHODS =====

    /// <summary>
    /// Creates or gets an existing user by slug.
    /// </summary>
    public async Task<(int Id, string Slug)> CreateOrGetUserAsync(string slug, string? displayName = null, string? mastodonUser = null, string? mastodonServer = null)
    {
        // Validate slug is not reserved
        if (LocalDbService.IsReservedUserSlug(slug))
        {
            throw new ArgumentException($"User slug '{slug}' is reserved and cannot be used.", nameof(slug));
        }

        using var connection = GetConnection();
        await connection.OpenAsync();

        // Check if user exists by slug
        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = "SELECT Id FROM Users WHERE Slug = @Slug";
        selectCommand.Parameters.AddWithValue("@Slug", slug);
        
        var existingId = await selectCommand.ExecuteScalarAsync();
        if (existingId != null)
        {
            return ((int)(long)existingId, slug);
        }

        // Check for duplicate Mastodon registration (same mastodonUser+mastodonServer with different slug)
        if (!string.IsNullOrWhiteSpace(mastodonUser) && !string.IsNullOrWhiteSpace(mastodonServer))
        {
            using var dupCheckCommand = connection.CreateCommand();
            dupCheckCommand.CommandText = @"
                SELECT Slug FROM Users 
                WHERE MastodonUser = @MastodonUser AND MastodonServer = @MastodonServer AND DeletedUtc IS NULL
            ";
            dupCheckCommand.Parameters.AddWithValue("@MastodonUser", mastodonUser);
            dupCheckCommand.Parameters.AddWithValue("@MastodonServer", mastodonServer);
            
            var existingSlug = await dupCheckCommand.ExecuteScalarAsync();
            if (existingSlug != null)
            {
                throw new ArgumentException($"Mastodon account {mastodonUser}@{mastodonServer} is already registered as '{existingSlug}'.", nameof(mastodonUser));
            }
        }

        // Create new user
        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = @"
            INSERT INTO Users (Slug, DisplayName, MastodonUser, MastodonServer, CreatedUtc, UpdatedUtc)
            VALUES (@Slug, @DisplayName, @MastodonUser, @MastodonServer, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            SELECT last_insert_rowid();
        ";
        insertCommand.Parameters.AddWithValue("@Slug", slug);
        insertCommand.Parameters.AddWithValue("@DisplayName", (object?)displayName ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("@MastodonUser", (object?)mastodonUser ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("@MastodonServer", (object?)mastodonServer ?? DBNull.Value);

        var newId = await insertCommand.ExecuteScalarAsync();
        return ((int)(long)newId!, slug);
    }

    /// <summary>
    /// Gets all active users.
    /// </summary>
    public async Task<List<(int Id, string Slug, string? DisplayName, string CreatedUtc)>> GetAllUsersAsync()
    {
        var users = new List<(int, string, string?, string)>();

        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Slug, DisplayName, CreatedUtc
            FROM Users
            WHERE DeletedUtc IS NULL
            ORDER BY CreatedUtc DESC
        ";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            var slug = reader.GetString(1);
            var displayName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var createdUtc = reader.GetString(3);
            users.Add((id, slug, displayName, createdUtc));
        }

        return users;
    }

    /// <summary>
    /// Gets a user by slug.
    /// </summary>
    public async Task<(int Id, string Slug, string? DisplayName, string? MastodonUser, string? MastodonServer)?> GetUserBySlugAsync(string slug)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Slug, DisplayName, MastodonUser, MastodonServer
            FROM Users
            WHERE Slug = @Slug AND DeletedUtc IS NULL
        ";
        command.Parameters.AddWithValue("@Slug", slug);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)
            );
        }

        return null;
    }

    /// <summary>
    /// Gets a user by Mastodon credentials.
    /// </summary>
    public async Task<(int Id, string Slug, string? DisplayName, string? MastodonUser, string? MastodonServer)?> GetUserByMastodonAsync(string mastodonUser, string mastodonServer)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Slug, DisplayName, MastodonUser, MastodonServer
            FROM Users
            WHERE MastodonUser = @MastodonUser AND MastodonServer = @MastodonServer AND DeletedUtc IS NULL
        ";
        command.Parameters.AddWithValue("@MastodonUser", mastodonUser);
        command.Parameters.AddWithValue("@MastodonServer", mastodonServer);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)
            );
        }

        return null;
    }

    /// <summary>
    /// Soft-deletes a user by slug.
    /// </summary>
    public async Task<bool> DeleteUserAsync(string slug)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Users
            SET DeletedUtc = CURRENT_TIMESTAMP, UpdatedUtc = CURRENT_TIMESTAMP
            WHERE Slug = @Slug AND DeletedUtc IS NULL
        ";
        command.Parameters.AddWithValue("@Slug", slug);

        var affected = await command.ExecuteNonQueryAsync();
        return affected > 0;
    }

    /// <summary>
    /// Ensures the admin user exists in the main database.
    /// </summary>
    public async Task EnsureAdminUserExistsAsync(string? adminMastodonUser = null, string? adminMastodonDomain = null)
    {
        const string adminSlug = "root";
        string adminDisplayName;
        
        if (!string.IsNullOrWhiteSpace(adminMastodonUser) && !string.IsNullOrWhiteSpace(adminMastodonDomain))
        {
            adminDisplayName = $"Admin ({adminMastodonUser}@{adminMastodonDomain})";
        }
        else
        {
            adminDisplayName = "Administrator";
        }
        
        var existingUser = await GetUserBySlugAsync(adminSlug);
        if (existingUser == null)
        {
            await CreateOrGetUserAsync(adminSlug, adminDisplayName, adminMastodonUser, adminMastodonDomain);
            Console.WriteLine($"[Information] Created admin user: {adminSlug}");
        }
    }

    /// <summary>
    /// Initializes a new user by:
    /// 1. Creating the user entry in the main database (domain.db)
    /// 2. Creating a user-specific database file
    /// 3. Initializing all tables in the user database
    /// </summary>
    public async Task<(int UserId, string UserSlug)> InitializeNewUserAsync(string domain, string userSlug, string? displayName = null, string? mastodonUser = null, string? mastodonServer = null)
    {
        // Normalize inputs
        domain = domain.Trim().ToLowerInvariant().TrimEnd('/').Split(':')[0];
        userSlug = userSlug.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Domain must not be null or empty.", nameof(domain));

        if (string.IsNullOrWhiteSpace(userSlug))
            throw new ArgumentException("User slug must not be null or empty.", nameof(userSlug));

        displayName = displayName?.Trim() ?? userSlug;

        Console.WriteLine($"[Information] Initializing new user: {userSlug} on domain {domain}");

        // 1. Create/get user in main database
        var (userId, slug) = await CreateOrGetUserAsync(userSlug, displayName, mastodonUser, mastodonServer);

        // 2. Create user-specific database file
        var userDbFileName = $"{domain}_{userSlug}.db";
        var userDbPath = LocalDbService.GetDbPath(userDbFileName);
        bool userDbExisted = File.Exists(userDbPath);

        Console.WriteLine($"Domain/userslug: {domain}/{userSlug}");

        // 3. Create/initialize user database with all necessary tables
        var userDb = new UserScopedDb(domain, userSlug, autoCreate: true);

        // Verify tables were created
        using var userConnection = userDb.GetConnection();
        await userConnection.OpenAsync();
        var tables = GetTableNames(userConnection);

        if (tables.Count == 0)
        {
            throw new InvalidOperationException($"User database tables were not properly initialized for {userSlug}");
        }

        Console.WriteLine($"[Information] Successfully initialized new user '{userSlug}' with database '{userDbFileName}'. Tables created: {string.Join(", ", tables)}");

        // 4. Generate actor keys if they don't exist yet
        var (existingPubKey, _) = await userDb.GetActorKeysAsync();
        if (string.IsNullOrEmpty(existingPubKey))
        {
            var keyPair = await CryptoService.GenerateKeyPairAsync();
            await userDb.SetActorKeysAsync(keyPair.PublicKeyPem, keyPair.PrivateKeyPem);
            Console.WriteLine($"[Information] Generated actor keys for user '{userSlug}'");
        }

        return (userId, slug);
    }

    /// <summary>
    /// Gets the list of table names in the database.
    /// </summary>
    private List<string> GetTableNames(SQLiteConnection connection)
    {
        var tables = new List<string>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Error retrieving table names: {ex.Message}");
        }

        return tables;
    }

    public async Task<bool> IsAdminMastodonAsync(string username, string domain)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        return username == "root";
    }

    // ===== FOLLOWING INDEX METHODS =====

    /// <summary>
    /// Ensures the domain-level Following table exists.
    /// Called during startup/migration.
    /// </summary>
    public void EnsureFollowingTable()
    {
        try
        {
            using var connection = GetConnection();
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Following (
                    UserSlug TEXT NOT NULL COLLATE NOCASE,
                    ActorUrl TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (UserSlug, ActorUrl)
                );
                CREATE INDEX IF NOT EXISTS IX_Following_ActorUrl ON Following (ActorUrl);
            ";
            cmd.ExecuteNonQuery();
            connection.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Error ensuring Following table: {ex.Message}");
        }
    }

    /// <summary>
    /// Records that a local user is following a remote actor.
    /// Duplicates are silently ignored (INSERT OR IGNORE).
    /// </summary>
    public async Task AddFollowingAsync(string userSlug, string actorUrl)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO Following (UserSlug, ActorUrl)
            VALUES (@userSlug, @actorUrl);
        ";
        cmd.Parameters.AddWithValue("@userSlug", userSlug);
        cmd.Parameters.AddWithValue("@actorUrl", actorUrl);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes a follow relationship for a local user.
    /// </summary>
    public async Task RemoveFollowingAsync(string userSlug, string actorUrl)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Following WHERE UserSlug = @userSlug AND ActorUrl = @actorUrl;";
        cmd.Parameters.AddWithValue("@userSlug", userSlug);
        cmd.Parameters.AddWithValue("@actorUrl", actorUrl);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Returns all local user slugs that follow the given remote actor.
    /// Used by the shared inbox to fan-out activities.
    /// </summary>
    public async Task<List<string>> GetFollowersOfActorAsync(string actorUrl)
    {
        using var connection = GetConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT UserSlug FROM Following WHERE ActorUrl = @actorUrl;";
        cmd.Parameters.AddWithValue("@actorUrl", actorUrl);
        var result = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }
}
