namespace FediProfile.Core;

using System.Text.Json;

public record IntegrationRef(string Name, string Url)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Decode from base64url query-param value. Returns null on failure.</summary>
    public static IntegrationRef? FromQueryParam(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var json = Convert.FromBase64String(
                encoded.Replace('-', '+').Replace('_', '/').PadRight(
                    encoded.Length + (4 - encoded.Length % 4) % 4, '='));
            var obj = JsonSerializer.Deserialize<IntegrationRef>(json, JsonOpts);
            // Validate: name must be non-empty, url must be a valid absolute URI
            if (obj != null
                && !string.IsNullOrWhiteSpace(obj.Name)
                && Uri.TryCreate(obj.Url, UriKind.Absolute, out var uri)
                && (uri.Scheme == "https" || uri.Scheme == "http"))
            {
                return obj;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Encode to a base64url string safe for query params.</summary>
    public string ToQueryParam()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this, JsonOpts);
        return Convert.ToBase64String(json)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Append ?ref= (or &amp;ref=) to an existing URL string.</summary>
    public string AppendTo(string url)
    {
        var sep = url.Contains('?') ? "&" : "?";
        return $"{url}{sep}ref={Uri.EscapeDataString(ToQueryParam())}";
    }
}
