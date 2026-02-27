using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using FediProfile.Services;

namespace Microsoft.AspNetCore.Routing;

internal static class LoginLogoutEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps login/logout endpoints at /login and /logout.
    /// Users are identified by their authenticated session, not by URL path.
    /// </summary>
    internal static IEndpointConventionBuilder MapLoginAndLogout(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("");

        group.MapGet("/login/oauth/{server}", (string server, string? returnUrl) =>
        {
            var authProps = GetAuthProperties(returnUrl ?? "/admin");
            authProps.Items["mastodon_server"] = server;
            return TypedResults.Challenge(authProps, new[] { "DynamicMastodon" });
        }).AllowAnonymous();

        group.MapGet("/logout", (string? returnUrl) =>
        {
            return TypedResults.SignOut(
                GetAuthProperties(returnUrl ?? "/"),
                new[] { CookieAuthenticationDefaults.AuthenticationScheme });
        });

        group.MapPost("/logout", ([FromForm] string? returnUrl) =>
        {
            return TypedResults.SignOut(
                GetAuthProperties(returnUrl ?? "/"),
                new[] { CookieAuthenticationDefaults.AuthenticationScheme });
        });

        return group;
    }

    /// <summary>
    /// Maps registration OAuth endpoints for the public registration flow.
    /// Creates endpoints like:
    /// - /register/oauth/{server} (GET)
    /// </summary>
    internal static IEndpointConventionBuilder MapRegistrationOAuth(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/register");

        group.MapGet("/oauth/{server}", (string server, string? returnUrl) =>
        {
            var authProps = GetAuthProperties("/register/choose-username");
            authProps.Items["mastodon_server"] = server;
            authProps.Items["registration_flow"] = "true";
            return TypedResults.Challenge(authProps, new[] { "DynamicMastodon" });
        }).AllowAnonymous();

        return group;
    }

    private static AuthenticationProperties GetAuthProperties(string? returnUrl)
    {
        const string pathBase = "/";

        if (string.IsNullOrEmpty(returnUrl))
        {
            returnUrl = pathBase;
        }
        else if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        {
            returnUrl = new Uri(returnUrl, UriKind.Absolute).PathAndQuery;
        }
        else if (returnUrl[0] != '/')
        {
            returnUrl = $"{pathBase}{returnUrl}";
        }

        return new AuthenticationProperties { RedirectUri = returnUrl };
    }
}
