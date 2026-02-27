using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using FediProfile.Services;

namespace FediProfile.Identity;

public static class MastodonOAuthExtensions
{
    private static readonly Dictionary<string, (string clientId, string clientSecret)> _mastodonAppCache = new();
    private static readonly SemaphoreSlim _registrationSemaphore = new(1, 1);

    public static AuthenticationBuilder AddDynamicMastodon(
        this AuthenticationBuilder builder,
        Action<OAuthOptions> configureOptions,
        LocalDbFactory localDbFactory)
    {
        return builder.AddOAuth("DynamicMastodon", "Mastodon (Dynamic)", o =>
        {
            o.AuthorizationEndpoint = "https://placeholder.invalid/oauth/authorize";
            o.TokenEndpoint = "https://placeholder.invalid/oauth/token";
            o.UserInformationEndpoint = "https://placeholder.invalid/api/v1/accounts/verify_credentials";

            o.CallbackPath = new Microsoft.AspNetCore.Http.PathString("/signin-mastodon-dynamic");

            o.ClientId = "placeholder";
            o.ClientSecret = "placeholder";

            o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
            o.ClaimActions.MapJsonKey(ClaimTypes.Name, "username");
            o.ClaimActions.MapJsonKey("urn:mastodon:id", "id");
            o.ClaimActions.MapJsonKey("urn:mastodon:username", "username");

            o.Events = new OAuthEvents
            {
                OnRedirectToAuthorizationEndpoint = async context =>
                {
                    var hostname = context.Properties.Items.TryGetValue("mastodon_server", out var storedServer)
                        ? storedServer
                        : throw new InvalidOperationException("Mastodon server not specified");

                    var (clientId, clientSecret) = await GetOrRegisterMastodonAppAsync(hostname, context.HttpContext.RequestServices);

                    context.Properties.Items["mastodon_client_id"] = clientId;
                    context.Properties.Items["mastodon_client_secret"] = clientSecret;
                    context.Properties.Items["mastodon_hostname"] = hostname;

                    var authUrl = $"https://{hostname}/oauth/authorize";
                    var request = context.HttpContext.Request;
                    var fullRedirectUri = $"{request.Scheme}://{request.Host}{context.Options.CallbackPath}";

                    var queryParams = new Dictionary<string, string>
                    {
                        ["client_id"] = clientId,
                        ["redirect_uri"] = fullRedirectUri,
                        ["response_type"] = "code",
                        ["scope"] = string.Join(" ", context.Options.Scope)
                    };

                    var state = context.Options.StateDataFormat.Protect(context.Properties);
                    queryParams["state"] = state;

                    var newRedirectUri = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(authUrl, queryParams);
                    context.Response.Redirect(newRedirectUri);
                },

                OnCreatingTicket = async context =>
                {
                    var hostname = context.Properties.Items.TryGetValue("mastodon_server", out var storedServer)
                        ? storedServer
                        : throw new InvalidOperationException("Mastodon server not found in properties");
                    var userInfoEndpoint = $"https://{hostname}/api/v1/accounts/verify_credentials";
                    
                    var domain = context.HttpContext.Request.Host.Host;
                    var isMainAdmin = context.Properties.Items.ContainsKey("is_main_admin") && 
                                      context.Properties.Items["is_main_admin"] == "true";
                    
                    // Get database based on context (main admin or user-scoped)
                    var userContextAccessor = context.HttpContext.RequestServices.GetService<UserContextAccessor>();
                    var userSlug = userContextAccessor?.GetUserSlug();
                    var factory = context.HttpContext.RequestServices.GetService<LocalDbFactory>();
                    
                    // Always use domain database for admin check (admin settings are domain-level)
                    var domainDb = factory?.GetInstance(domain);

                    if (domainDb == null)
                    {
                        throw new InvalidOperationException("Failed to get database instance from LocalDbFactory");
                    }

                    var request = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();

                    if (context.Options.SaveTokens)
                    {
                        context.Properties.StoreTokens(new[]
                        {
                            new AuthenticationToken { Name = "access_token", Value = context.AccessToken }
                        });
                    }

                    using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    context.RunClaimActions(user.RootElement);

                    // Always add hostname claim so it's available in all flows (registration, admin, etc.)
                    var commonClaims = new List<Claim>
                    {
                        new Claim("urn:mastodon:hostname", hostname)
                    };

                    var username = context.Identity?.FindFirst(ClaimTypes.Name)?.Value;
                    var isAdmin = !string.IsNullOrWhiteSpace(username) &&
                        await domainDb.IsAdminMastodonAsync(username, hostname);

                    if (isAdmin)
                    {
                        commonClaims.Add(new Claim(ClaimTypes.Role, "admin"));

                        // Add user slug claim only if not main admin
                        if (isMainAdmin)
                        {
                            commonClaims.Add(new Claim("urn:fedi:admin_type", "main"));
                        }
                        else if (!string.IsNullOrEmpty(userSlug))
                        {
                            commonClaims.Add(new Claim("urn:fedi:user_slug", userSlug));
                            commonClaims.Add(new Claim("urn:fedi:admin_type", "user"));
                        }
                    }

                    context.Principal?.AddIdentity(new ClaimsIdentity(commonClaims));
                },

                OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    var isMainAdmin = context.Properties.Items.ContainsKey("is_main_admin") && 
                                      context.Properties.Items["is_main_admin"] == "true";
                    
                    if (isMainAdmin)
                    {
                        context.Response.Redirect("/admin/denied");
                    }
                    else
                    {
                        var userContextAccessor = context.HttpContext.RequestServices.GetService<UserContextAccessor>();
                        var userSlug = userContextAccessor?.GetUserSlug();
                        var deniedPath = !string.IsNullOrEmpty(userSlug) ? $"/{userSlug}/admin/denied" : "/denied";
                        context.Response.Redirect(deniedPath);
                    }
                    return Task.FromResult(0);
                }
            };

            configureOptions(o);
        });
    }

    private static async Task<(string clientId, string clientSecret)> GetOrRegisterMastodonAppAsync(
        string hostname,
        IServiceProvider services)
    {
        if (_mastodonAppCache.TryGetValue(hostname, out var cached))
        {
            return cached;
        }

        await _registrationSemaphore.WaitAsync();
        try
        {
            if (_mastodonAppCache.TryGetValue(hostname, out cached))
            {
                return cached;
            }

            var registrationService = services.GetRequiredService<MastodonRegistrationService>();
            var appDoc = await registrationService.RegisterApplicationAsync(hostname);

            var clientId = appDoc.RootElement.GetProperty("client_id").GetString();
            var clientSecret = appDoc.RootElement.GetProperty("client_secret").GetString();

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                throw new InvalidOperationException($"Failed to register Mastodon app for {hostname}: missing credentials");
            }

            var credentials = (clientId, clientSecret);
            _mastodonAppCache[hostname] = credentials;

            return credentials;
        }
        finally
        {
            _registrationSemaphore.Release();
        }
    }
}
