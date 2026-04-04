using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;
using FediProfile.Core;
using FediProfile.Models;
using FediProfile.Services;
using FediProfile.Components;
using FediProfile.Identity;

var builder = WebApplication.CreateBuilder(args);

// Setup logging
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.AddDebug();
});

builder.Services.AddHttpClient();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers();
builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();

// Register multi-tenant, multi-user database factory
var localDbFactory = new LocalDbFactory();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<LocalDbFactory>(localDbFactory);
builder.Services.AddScoped<UserContextAccessor>();

// Register split scoped databases
// DomainScopedDb: One domain database per domain (e.g., localhost.db, example.com.db)
// Contains: users table with OAuth credentials, actor keys, settings
builder.Services.AddScoped<DomainScopedDb>();

// UserScopedDb: Per-user database scoped to current user (e.g., localhost_maho.db)
// Contains: profile information (badges, links, followers, etc.)
// Automatically falls back to DomainScopedDb if user doesn't exist or not in user context
builder.Services.AddScoped<UserScopedDb>();



// Register services
builder.Services.AddSingleton<ActorService>();
builder.Services.AddScoped<FollowService>();
builder.Services.AddScoped<AnnounceService>();
builder.Services.AddSingleton<ProfileHtmlService>();

// Register job processing services
builder.Services.AddScoped<JobProcessor>();
builder.Services.AddHostedService<JobExecutor>();

// Detect listen port for localhost fallback domain
var listenUrl = (builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000")
    .Split(';')[0];
// Kestrel allows http://+:80, http://*:5000, http://0.0.0.0:5000 etc. — normalise to a parseable URI
var normalisedUrl = listenUrl.Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("://0.0.0.0", "://localhost");
var fallbackDomain = Uri.TryCreate(normalisedUrl, UriKind.Absolute, out var listenUri) && listenUri.Port is not (80 or 443)
    ? $"localhost:{listenUri.Port}"
    : "localhost";

// Add Mastodon registration service
builder.Services.AddScoped<MastodonRegistrationService>(provider =>
{
    var httpClient = provider.GetRequiredService<HttpClient>();
    var config = provider.GetRequiredService<IConfiguration>();
    var domains = config.GetSection("Domains").Get<string[]>()
        ?? new[] { fallbackDomain };

    var primaryDomain = domains.First();
    var primaryScheme = primaryDomain.Contains("localhost") ? "http" : "https";
    var website = $"{primaryScheme}://{primaryDomain}";

    var redirectUris = new List<string> { "urn:ietf:wg:oauth:2.0:oob" };
    foreach (var domain in domains)
    {
        Console.WriteLine($"Adding Mastodon redirect URIs for domain: {domain}");
        redirectUris.Add($"https://{domain}/signin-mastodon-dynamic");
        redirectUris.Add($"http://{domain}/signin-mastodon-dynamic");
    }

    return new MastodonRegistrationService(httpClient, "FediProfile", website, redirectUris.ToArray());
});

var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(option =>
    {
        // Default paths, but will be overridden by user-scoped paths
        option.LoginPath = "/login";
        option.LogoutPath = "/logout";
        option.AccessDeniedPath = "/admin/denied";
    });

auth.AddDynamicMastodon(o =>
{
    o.Scope.Add("read");
    o.SaveTokens = true;
}, localDbFactory);

// Conditionally add GitHub OAuth if credentials are configured
var ghClientId = builder.Configuration["Authentication:GitHub:ClientId"];
var ghClientSecret = builder.Configuration["Authentication:GitHub:ClientSecret"];
if (!string.IsNullOrEmpty(ghClientId) && !string.IsNullOrEmpty(ghClientSecret))
{
    auth.AddOAuth("GitHub", o =>
    {
        o.ClientId = ghClientId;
        o.ClientSecret = ghClientSecret;
        o.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        o.TokenEndpoint = "https://github.com/login/oauth/access_token";
        o.UserInformationEndpoint = "https://api.github.com/user";
        o.CallbackPath = "/signin-github";
        o.SaveTokens = true;
        o.Scope.Add("read:user");

        o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        o.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");

        o.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("User-Agent", "FediProfile");

                var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                context.RunClaimActions(user.RootElement);

                var ghName      = user.RootElement.TryGetProperty("name",       out var ghNameEl)   ? ghNameEl.GetString()   : null;
                var ghAvatarUrl = user.RootElement.TryGetProperty("avatar_url", out var ghAvatarEl) ? ghAvatarEl.GetString() : null;

                var ghClaims = new List<System.Security.Claims.Claim>
                {
                    new("urn:mastodon:hostname", "github.com"),
                };
                if (!string.IsNullOrWhiteSpace(ghName))
                    ghClaims.Add(new("urn:github:display_name", ghName));
                if (!string.IsNullOrWhiteSpace(ghAvatarUrl))
                    ghClaims.Add(new("urn:github:avatar_url", ghAvatarUrl));

                context.Identity?.AddClaims(ghClaims);
            }
        };
    });
}

// Conditionally add LinkedIn OAuth if credentials are configured
var liClientId = builder.Configuration["Authentication:LinkedIn:ClientId"];
var liClientSecret = builder.Configuration["Authentication:LinkedIn:ClientSecret"];
if (!string.IsNullOrEmpty(liClientId) && !string.IsNullOrEmpty(liClientSecret))
{
    auth.AddOAuth("LinkedIn", o =>
    {
        o.ClientId = liClientId;
        o.ClientSecret = liClientSecret;
        o.AuthorizationEndpoint = "https://www.linkedin.com/oauth/v2/authorization";
        o.TokenEndpoint = "https://www.linkedin.com/oauth/v2/accessToken";
        o.UserInformationEndpoint = "https://api.linkedin.com/rest/identityMe";
        o.CallbackPath = "/signin-linkedin";
        o.SaveTokens = true;
        o.UsePkce = false;
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.Scope.Add("r_profile_basicinfo");

        o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        o.ClaimActions.MapJsonKey(ClaimTypes.Name, "localizedFirstName");
        o.ClaimActions.MapJsonKey(ClaimTypes.Surname, "localizedLastName");

        // All claims set manually in OnCreatingTicket — identityMe response is not OIDC userinfo.

        o.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                // Fetch identityMe — returns basicInfo with localized name, picture, and profileUrl
                var uiReq = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                uiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
                uiReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                // LinkedIn REST APIs require a version header
                uiReq.Headers.Add("LinkedIn-Version", "202510");

                var uiResp = await context.Backchannel.SendAsync(uiReq, context.HttpContext.RequestAborted);
                uiResp.EnsureSuccessStatusCode();

                using var uiDoc = JsonDocument.Parse(await uiResp.Content.ReadAsStringAsync());
                var root = uiDoc.RootElement;

                // Helper: extract the preferred-locale value from a LinkedIn localized field
                // Structure: { "localized": { "en_US": "John" }, "preferredLocale": { "language": "en", "country": "US" } }
                Func<JsonElement, string?> getLocalized = field =>
                {
                    if (field.ValueKind != JsonValueKind.Object) return null;
                    if (!field.TryGetProperty("localized", out var localizedEl)) return null;
                    if (field.TryGetProperty("preferredLocale", out var localeEl))
                    {
                        var lang    = localeEl.TryGetProperty("language", out var langEl)    ? langEl.GetString()    : null;
                        var country = localeEl.TryGetProperty("country",  out var countryEl) ? countryEl.GetString() : null;
                        if (!string.IsNullOrEmpty(lang) && !string.IsNullOrEmpty(country))
                        {
                            var key = $"{lang}_{country}";
                            if (localizedEl.TryGetProperty(key, out var locVal) && locVal.ValueKind == JsonValueKind.String)
                                return locVal.GetString();
                        }
                    }
                    // Fall back to the first available locale value
                    foreach (var prop in localizedEl.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
                    return null;
                };

                // identityMe response: id and all profile fields are inside basicInfo, not at root
                string? id         = null;
                string? fullName   = null;
                string? picture    = null;
                string? profileUrl = null;

                if (root.TryGetProperty("basicInfo", out var basicInfo))
                {
                    id = basicInfo.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                    var firstName = basicInfo.TryGetProperty("firstName", out var fnEl) ? getLocalized(fnEl) : null;
                    var lastName  = basicInfo.TryGetProperty("lastName",  out var lnEl) ? getLocalized(lnEl) : null;

                    if (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName))
                        fullName = $"{firstName} {lastName}".Trim();

                    if (basicInfo.TryGetProperty("profilePicture", out var picEl) &&
                        picEl.TryGetProperty("croppedImage", out var croppedEl) &&
                        croppedEl.TryGetProperty("downloadUrl", out var dlUrl))
                        picture = dlUrl.GetString();

                    if (basicInfo.TryGetProperty("profileUrl", out var urlEl))
                        profileUrl = urlEl.GetString();
                }

                // Build loginName: sanitised full name > id
                string loginName;
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    loginName = System.Text.RegularExpressions.Regex.Replace(
                        fullName.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
                }
                else
                {
                    loginName = id ?? "linkedin-user";
                }

                var claims = new List<System.Security.Claims.Claim>
                {
                    new(ClaimTypes.NameIdentifier, id ?? loginName),
                    new(ClaimTypes.Name, loginName),
                    new("urn:mastodon:hostname", "linkedin.com"),
                };
                if (!string.IsNullOrWhiteSpace(fullName))
                    claims.Add(new("urn:linkedin:display_name", fullName));
                if (!string.IsNullOrWhiteSpace(picture))
                    claims.Add(new("urn:linkedin:picture", picture));
                if (!string.IsNullOrWhiteSpace(profileUrl))
                    claims.Add(new("urn:linkedin:profile_url", profileUrl));

                context.Identity?.AddClaims(claims);
            }
        };
    });
}

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseRouting();
app.UseStaticFiles();

// Initialize all domains from configuration
var config = builder.Configuration;
var domains = config.GetSection("Domains").Get<string[]>() ?? new[] { fallbackDomain };

foreach (var domain in domains)
{
    await InitializeDomainAsync(domain, config, localDbFactory, app.Logger);
}

var actorService = app.Services.GetRequiredService<ActorService>();

// Helper: serve the styled 404 page for browser requests
IResult NotFoundPage(IWebHostEnvironment env)
{
    var notFoundPath = Path.Combine(env.WebRootPath, "404.html");
    if (File.Exists(notFoundPath))
    {
        return Results.Content(File.ReadAllText(notFoundPath), "text/html", statusCode: 404);
    }
    return Results.NotFound("Not found");
}

// Root landing page (/) - multitenant static page with memory + browser cache.
// Lookup order: wwwroot/{domain}/landing.html → wwwroot/landing.html → hardcoded fallback.
// Domain directories use _ instead of : so the path is valid on all platforms
// (e.g. localhost_5000, hub.badgefed.org).
app.MapGet("/", async (HttpRequest request, HttpResponse response, IWebHostEnvironment env, IMemoryCache cache) =>
{
    var host = request.Host.Host;
    if (request.Host.Port.HasValue && request.Host.Port != 80 && request.Host.Port != 443)
        host = $"{request.Host.Host}:{request.Host.Port}";

    // Replace colon with underscore — colon is illegal in Windows directory names
    var domainDir = host.Replace(":", "_");
    var cacheKey  = $"landing:{host}";

    if (!cache.TryGetValue<(string Content, string Etag)>(cacheKey, out var cached))
    {
        var domainPath   = Path.Combine(env.WebRootPath, domainDir, "landing.html");
        var fallbackPath = Path.Combine(env.WebRootPath, "landing.html");

        var filePath = File.Exists(domainPath)   ? domainPath
                     : File.Exists(fallbackPath) ? fallbackPath
                     : null;

        string content, etag;

        if (filePath is null)
        {
            content = "<!DOCTYPE html><html><head><title>FediProfile</title><meta charset='utf-8'/><meta name='viewport' content='width=device-width,initial-scale=1'/></head><body><h1>FediProfile</h1><p>A federated profile system.</p></body></html>";
            etag    = "\"default\"";
        }
        else
        {
            content = await File.ReadAllTextAsync(filePath);
            etag    = $"\"{new FileInfo(filePath).LastWriteTimeUtc.Ticks:x}\"";
        }

        cached = (content, etag);
        cache.Set(cacheKey, cached, TimeSpan.FromMinutes(5));
    }

    // Honour conditional GET — avoids re-sending unchanged content
    if (request.Headers.IfNoneMatch.ToString() == cached.Etag)
    {
        response.StatusCode = StatusCodes.Status304NotModified;
        return;
    }

    response.Headers.CacheControl = "public, max-age=300";
    response.Headers.ETag         = cached.Etag;
    response.ContentType          = "text/html; charset=utf-8";
    await response.WriteAsync(cached.Content);
});

// Map ActivityPub actor endpoint: /{user} or /{user}/ 
app.MapGet("/{userSlug}", async (HttpRequest request, string userSlug, LocalDbFactory factory, ActorService actorService, IWebHostEnvironment env) =>
{
    var accept = request.Headers.Accept.ToString();
    var domain = request.Host.Host;
    if (request.Host.Port.HasValue && request.Host.Port != 80 && request.Host.Port != 443)
    {
        domain = $"{domain}:{request.Host.Port}";
    }

    Console.WriteLine($"Received request for /{userSlug} with Accept: {accept} from {request.HttpContext.Connection.RemoteIpAddress} agent {request.Headers["User-Agent"]}");

    // Check if user exists in main database
    var mainDb = factory.GetInstance(domain);
    var user = await mainDb.GetUserBySlugAsync(userSlug);
    
    if (user == null)
    {
        return NotFoundPage(env);
    }
   
    if (ActivityPubHelper.IsActivityPubRequest(accept))
    {
        // Serve pre-generated static actor JSON if available.
        // The file is regenerated when the user saves their profile/links (same pattern as profiles/{userSlug}.html).
        var actorJsonPath = Path.Combine(env.WebRootPath, "profiles", $"{userSlug}.json");
        if (File.Exists(actorJsonPath))
        {
            var jsonString = await File.ReadAllTextAsync(actorJsonPath);
            return Results.Content(jsonString, "application/activity+json");
        }

        // Fallback: build on-the-fly (first request before profile is saved, or file was deleted)
        var userDb = factory.GetInstance(domain, userSlug, autoCreate: false);
        if (!File.Exists(userDb.DbPath))
        {
            return NotFoundPage(env);
        }

        var actor = await actorService.BuildActorAsync(userDb, request, verifiedUris: (await mainDb.GetVerifiedUrisAsync(userSlug)).Select(v => v.Uri).ToList());
        var actorJson = JsonSerializer.Serialize(actor, ActorService.ActorJsonOptions);

        // Write the file so subsequent requests are served from disk
        try
        {
            var profilesDir = Path.Combine(env.WebRootPath, "profiles");
            Directory.CreateDirectory(profilesDir);
            await File.WriteAllTextAsync(actorJsonPath, actorJson);
        }
        catch { /* best-effort cache write */ }

        return Results.Content(actorJson, "application/activity+json");
    }

    // Serve pre-generated static profile if available (includes rel="me" links,
    // OpenGraph meta, theme). Falls back to generic profile.html for new users
    // whose profile hasn't been saved yet.
    var staticPath = Path.Combine(env.WebRootPath, "profiles", $"{userSlug}.html");
    var filePath = File.Exists(staticPath) ? staticPath : Path.Combine(env.WebRootPath, "profile.html");
    return Results.File(filePath, "text/html");
});

// Keep .well-known endpoints as they are (not user-scoped for now)
app.MapGet("/.well-known/webfinger", async (HttpRequest request, LocalDbFactory factory) =>
{
    var resource = request.Query["resource"].ToString();

    if (string.IsNullOrEmpty(resource) || !resource.StartsWith("acct:"))
    {
        return Results.BadRequest("Invalid resource parameter");
    }

    var domain = request.Host.Host;
    if (request.Host.Port.HasValue && request.Host.Port != 80 && request.Host.Port != 443)
    {
        domain = $"{domain}:{request.Host.Port}";
    }

    var account = resource.Substring("acct:".Length);
    var parts = account.Split('@');

    if (parts.Length != 2 || parts[1] != domain)
    {
        return Results.NotFound("Account invalid or not found");
    }

    // Query main database for this user
    var mainDb = factory.GetInstance(domain);
    var user = await mainDb.GetUserBySlugAsync(parts[0]);
    
    if (user == null)
    {
        return Results.NotFound("Account or domain not found");
    }

    var username = parts[0];
    var scheme = request.Scheme;
    var actorId = $"{scheme}://{domain}/{username}";
    var subject = $"acct:{username}@{domain}";
    var aliases = new[] { actorId };

    var links = new object[]
    {
        new
        {
            rel = "self",
            type = "application/activity+json",
            href = actorId
        },
        new
        {
            rel = "http://webfinger.net/rel/profile-page",
            type = "text/html",
            href = actorId
        }
    };

    var result = Results.Json(new { subject, aliases, links }, contentType: "application/jrd+json");
    return result;
});

app.MapGet("/.well-known/nodeinfo", () =>
{
    var domain = "example.com";
    var links = new object[]
    {
        new
        {
            rel = "http://nodeinfo.diaspora.software/ns/schema/2.1",
            href = $"https://{domain}/nodeinfo/2.1"
        }
    };

    var result = Results.Json(new { links });
    return result;
});

// ===== MAIN DOMAIN ADMIN ENDPOINTS =====
// These must be registered BEFORE /{userSlug} routes to avoid routing conflicts


// User-scoped links endpoint: /{user}/links
app.MapGet("/{userSlug}/links", async (UserScopedDb db) =>
{
    var links = await db.GetLinksAsync();
    return Results.Json(links);
});

// User-scoped badges endpoint: /{user}/badges (public - hidden badges excluded)
app.MapGet("/{userSlug}/badges", async (UserScopedDb db) =>
{
    var badges = await db.GetReceivedBadgesAsync();
    var visible = badges.Where(b => !b.Hidden).Select(b => new {
        b.Title,
        b.Image,
        b.Description,
        b.IssuedOn,
        b.NoteId
    });
    return Results.Json(visible);
});

// User-scoped recent posts endpoint: /{user}/recent-posts (public, respects ShowRecentPosts setting)
app.MapGet("/{userSlug}/recent-posts", async (UserScopedDb db) =>
{
    var settings = await db.GetSettingsAsync();
    if (settings == null || !settings.ShowRecentPosts)
    {
        return Results.Json(Array.Empty<object>());
    }

    var posts = await db.GetRecentPostsAsync();
    var result = posts.Select(p => new {
        p.Content,
        p.Summary,
        p.Url,
        p.PublishedUtc,
        p.BoostedUtc
    });
    return Results.Json(result);
});

// ActivityPub followers collection: /{user}/followers
app.MapGet("/{userSlug}/followers", async (HttpRequest request, UserScopedDb db) =>
{
    var accept = request.Headers.Accept.ToString();

    if (!ActivityPubHelper.IsActivityPubRequest(accept))
    {
        return Results.BadRequest("This endpoint only supports ActivityPub requests");
    }

    var domain = request.Host.ToString();
    var scheme = request.Scheme;
    var userSlug = (string)request.RouteValues["userSlug"];

    var emptyCollection = new ActivityPubCollection
    {
        Id = $"{scheme}://{domain}/{userSlug}/followers",
        Type = "OrderedCollection",
        TotalItems = 0,
        OrderedItems = Array.Empty<string>()
    };

    try
    {
        var followers = await db.GetFollowersAsync();

        if (followers == null || followers.Count == 0)
        {
            // Return empty collection if no followers
            return Results.Json(emptyCollection, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }, contentType: "application/activity+json");
        }
        
        var followerUris = followers.Select(f => f.FollowerUri).ToList();

        var collection = new ActivityPubCollection
        {
            Id = $"{scheme}://{domain}/{userSlug}/followers",
            Type = "OrderedCollection",
            TotalItems = followers.Count,
            OrderedItems = followerUris
        };

        return Results.Json(collection, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }, contentType: "application/activity+json");
    } catch (Exception ex)
    {
        Console.WriteLine($"Error in followers endpoint: {ex.Message}");
        // return empty collection
        return Results.Json(emptyCollection, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }, contentType: "application/activity+json");
    }
   
});

// ActivityPub following collection: /{user}/following
// Returns the ActorAPUri of all links with AutoBoost enabled (the actors we are boosting)
app.MapGet("/{userSlug}/following", async (HttpRequest request, UserScopedDb db) =>
{
    var accept = request.Headers.Accept.ToString();

    if (!ActivityPubHelper.IsActivityPubRequest(accept))
    {
        return Results.BadRequest("This endpoint only supports ActivityPub requests");
    }

    var domain = request.Host.ToString();
    var scheme = request.Scheme;
    var userSlug = (string)request.RouteValues["userSlug"];

    var autoBoostLinks = await db.GetAutoBoostLinksAsync();
    var followingUris = autoBoostLinks
        .Where(l => !string.IsNullOrEmpty(l.ActorAPUri))
        .Select(l => l.ActorAPUri!)
        .ToList();

    var collection = new ActivityPubCollection
    {
        Id = $"{scheme}://{domain}/{userSlug}/following",
        Type = "OrderedCollection",
        TotalItems = followingUris.Count,
        OrderedItems = followingUris
    };

    return Results.Json(collection, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    }, contentType: "application/activity+json");
});

// ActivityPub outbox: /{user}/outbox
app.MapGet("/{userSlug}/outbox", async (HttpRequest request) =>
{
    var accept = request.Headers.Accept.ToString();

    if (!ActivityPubHelper.IsActivityPubRequest(accept))
    {
        return Results.BadRequest("This endpoint only supports ActivityPub requests");
    }

    var domain = request.Host.ToString();
    var scheme = request.Scheme;
    var userSlug = (string)request.RouteValues["userSlug"];

    var collection = new ActivityPubCollection
    {
        Id = $"{scheme}://{domain}/{userSlug}/outbox",
        Type = "OrderedCollection",
        TotalItems = 0,
        OrderedItems = Array.Empty<object>()
    };

    return Results.Json(collection, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    }, contentType: "application/activity+json");
});


// Add middleware to handle dynamic Mastodon OAuth endpoints
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/signin-mastodon-dynamic" && context.Request.Query.ContainsKey("state"))
    {
        try
        {
            var authService = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
            var scheme = await authService.GetSchemeAsync("DynamicMastodon");
            if (scheme != null)
            {
                var options = context.RequestServices.GetService<IOptionsMonitor<OAuthOptions>>();
                var oauthOptions = options.Get("DynamicMastodon");

                var stateValue = context.Request.Query["state"];
                var properties = oauthOptions.StateDataFormat.Unprotect(stateValue);

                if (properties != null && properties.Items.TryGetValue("mastodon_server", out var hostname))
                {
                    oauthOptions.TokenEndpoint = $"https://{hostname}/oauth/token";
                    oauthOptions.UserInformationEndpoint = $"https://{hostname}/api/v1/accounts/verify_credentials";

                    if (MastodonOAuthExtensions.TryGetCachedCredentials(hostname, out var creds))
                    {
                        oauthOptions.ClientId = creds.clientId;
                        oauthOptions.ClientSecret = creds.clientSecret;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in dynamic Mastodon middleware: {ex.Message}");
        }
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapControllers();

// Map login/logout at root level: /login, /logout
app.MapLoginAndLogout();

// Map registration OAuth endpoints
app.MapRegistrationOAuth();

// Fallback: serve styled 404 page for any unmatched route
app.MapFallback((IWebHostEnvironment env) => NotFoundPage(env));

// ===== LOCAL FUNCTION: Domain Initialization =====
/// <summary>
/// Initializes a domain by:
/// 1. Creating/getting the domain database
/// 2. Initializing domain settings from configuration
/// 3. Creating the admin user account with user-specific database
/// 
/// This is extracted as a reusable method to support multi-domain initialization
/// and can be called again when adding single-step account creation.
/// </summary>
async Task InitializeDomainAsync(string domain, IConfiguration config, LocalDbFactory factory, ILogger logger)
{
    // Normalize domain
    domain = domain.Trim().ToLowerInvariant().TrimEnd('/').Split(':')[0];
    
    if (string.IsNullOrWhiteSpace(domain))
    {
        logger.LogWarning("Skipping initialization: Domain is null or empty");
        return;
    }

    logger.LogInformation("Initializing domain: {Domain}", domain);

    // 1. Get or create main database for this domain
    var mainDb = factory.GetInstance(domain);

    var adminMastodonUser = config["AdminAuthentication:MastodonUser"];
    var adminMastodonDomain = config["AdminAuthentication:MastodonDomain"];

    // 2. Initialize admin user in the linktree system
    const string adminUserSlug = "root";

    string adminDisplayName = !string.IsNullOrWhiteSpace(adminMastodonUser) && !string.IsNullOrWhiteSpace(adminMastodonDomain)
        ? $"Admin ({adminMastodonUser}@{adminMastodonDomain})"
        : "Administrator";

    try
    {
        if (string.IsNullOrWhiteSpace(adminMastodonUser) || string.IsNullOrWhiteSpace(adminMastodonDomain))
        {
            logger.LogWarning("Admin Mastodon credentials not fully configured. Admin user will be created without Mastodon authentication.");
        }

        // check if admin user already exists
        var existingAdmin = await mainDb.GetUserBySlugAsync(adminUserSlug);
        if (existingAdmin != null)
        {
            logger.LogInformation("Admin user '{AdminSlug}' already exists on domain {Domain} with ID {AdminUserId}",
                adminUserSlug, domain, existingAdmin.UserId);
            return;
        }

        // InitializeNewUserAsync creates:
        // - User entry in main database (domain.db)
        // - User-specific database (domain_root.db) with all default tables
        var (adminUserId, slug) = await mainDb.InitializeNewUserAsync(domain, adminUserSlug, adminDisplayName, adminMastodonUser, adminMastodonDomain, false);
        
        logger.LogInformation("Initialized admin user '{AdminSlug}' (ID: {AdminUserId}) on domain {Domain} authenticated as {MastodonUser}@{MastodonDomain}",
            slug, adminUserId, domain, adminMastodonUser ?? "unconfigured", adminMastodonDomain ?? "unconfigured");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize admin user '{AdminUserSlug}' on domain {Domain}", adminUserSlug, domain);
    }

    // 3. Apply pending migrations to ALL existing user databases for this domain
    try
    {
        var allUsers = await mainDb.GetAllUsersAsync();
        foreach (var (userId, userSlug, displayName, createdUtc) in allUsers)
        {
            try
            {
                // Instantiating UserScopedDb triggers EnsureCreated() → ApplyPendingMigrations()
                var userDb = factory.GetInstance(domain, userSlug);
                logger.LogInformation("Applied pending migrations to user DB: {Domain}_{UserSlug}", domain, userSlug);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to apply migrations for user '{UserSlug}' on domain {Domain}", userSlug, domain);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to enumerate users for migration on domain {Domain}", domain);
    }
}

app.Run();

