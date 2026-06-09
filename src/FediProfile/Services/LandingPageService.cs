using System.Net;
using System.Text;
using Markdig;

namespace FediProfile.Services;

/// <summary>
/// Generates and manages static domain landing pages from a shared HTML template.
/// </summary>
public class LandingPageService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LandingPageService> _logger;

    private const string TemplateFileName = "landing.template.html";

    private const string DefaultTemplate = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>{{INSTANCE_NAME}} - Welcome</title>
    <meta name="description" content="{{INSTANCE_NAME}} on FediProfile">
    <style>
        :root {
            --bg: #0b1220;
            --bg-soft: #121a2b;
            --ink: #eaf0ff;
            --muted: #adc2ef;
            --accent: #31c48d;
            --accent-2: #3b82f6;
            --card: rgba(255, 255, 255, 0.07);
            --border: rgba(255, 255, 255, 0.18);
        }

        * { box-sizing: border-box; }

        body {
            margin: 0;
            font-family: "Segoe UI", Tahoma, Geneva, Verdana, sans-serif;
            color: var(--ink);
            background:
                radial-gradient(circle at 10% 20%, rgba(59, 130, 246, 0.35), transparent 35%),
                radial-gradient(circle at 85% 10%, rgba(49, 196, 141, 0.28), transparent 36%),
                linear-gradient(155deg, var(--bg), #060912 58%, #0b1324);
            min-height: 100vh;
        }

        .wrap {
            max-width: 960px;
            margin: 0 auto;
            padding: 2rem 1rem 3rem;
        }

        .top {
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 1rem;
            margin-bottom: 1.75rem;
        }

        .brand {
            font-weight: 700;
            letter-spacing: 0.02em;
        }

        .actions {
            display: flex;
            gap: 0.6rem;
            flex-wrap: wrap;
        }

        .btn {
            display: inline-block;
            text-decoration: none;
            border: 1px solid var(--border);
            color: var(--ink);
            padding: 0.6rem 0.95rem;
            border-radius: 0.6rem;
            transition: transform 0.18s ease, border-color 0.18s ease;
            font-size: 0.92rem;
        }

        .btn:hover {
            transform: translateY(-1px);
            border-color: var(--accent-2);
        }

        .btn-primary {
            background: linear-gradient(135deg, var(--accent), var(--accent-2));
            border: none;
            color: #03131a;
            font-weight: 700;
        }

        .hero {
            border: 1px solid var(--border);
            border-radius: 1rem;
            padding: 2rem;
            background: linear-gradient(180deg, rgba(255, 255, 255, 0.12), rgba(255, 255, 255, 0.03));
            backdrop-filter: blur(8px);
            box-shadow: 0 20px 45px rgba(0, 0, 0, 0.35);
        }

        h1 {
            margin: 0 0 0.5rem;
            font-size: clamp(2rem, 4vw, 3.1rem);
            line-height: 1.12;
        }

        .lead {
            margin: 0;
            color: var(--muted);
            font-size: 1.04rem;
        }

        .content {
            margin-top: 1rem;
            border-top: 1px solid var(--border);
            padding-top: 1.25rem;
            line-height: 1.7;
            color: #eef4ff;
        }

        .content h2,
        .content h3 {
            margin-top: 1.4rem;
            margin-bottom: 0.6rem;
            line-height: 1.25;
        }

        .content p {
            margin: 0.75rem 0;
        }

        .content a {
            color: #8ec5ff;
        }

        .content code {
            background: rgba(0, 0, 0, 0.28);
            padding: 0.15rem 0.4rem;
            border-radius: 0.4rem;
        }

        .content pre {
            background: rgba(0, 0, 0, 0.35);
            padding: 0.85rem;
            border-radius: 0.6rem;
            overflow: auto;
        }

        .content blockquote {
            margin: 1rem 0;
            padding: 0.7rem 0.9rem;
            border-left: 3px solid var(--accent-2);
            background: rgba(59, 130, 246, 0.08);
        }

        footer {
            margin-top: 1.25rem;
            color: var(--muted);
            font-size: 0.88rem;
        }

        @media (max-width: 720px) {
            .hero { padding: 1.35rem; }
            .top { flex-direction: column; align-items: flex-start; }
        }
    </style>
</head>
<body>
    <div class="wrap">
        <div class="top">
            <div class="brand">{{INSTANCE_NAME}}</div>
            <div class="actions">
                <a class="btn" href="/">Home</a>
                <a class="btn" href="/admin/">Admin</a>
                <a class="btn btn-primary" href="/login">Create Profile</a>
            </div>
        </div>

        <section class="hero">
            <h1>{{INSTANCE_NAME}}</h1>
            <p class="lead">A federated profile hub you can own and operate.</p>
            <article class="content">
{{INSTANCE_MARKDOWN_HTML}}
            </article>
            <footer>Powered by FediProfile</footer>
        </section>
    </div>
</body>
</html>
""";

    private const string DefaultMarkdown = """
## Welcome

This instance is powered by FediProfile.

Use this page to share your mission, links, and onboarding notes for your community.

- Click **Create Profile** to get started.
- Follow local accounts and connect your Fediverse identity.
- Visit **Admin** to customize this landing page.
""";

    public LandingPageService(IWebHostEnvironment env, ILogger<LandingPageService> logger)
    {
        _env = env;
        _logger = logger;
    }

    public static string ResolveHost(HttpRequest request)
    {
        var host = request.Host.Host;
        if (request.Host.Port.HasValue && request.Host.Port is not (80 or 443))
        {
            host = $"{host}:{request.Host.Port.Value}";
        }

        return host;
    }

    public static string BuildCacheKey(string host)
    {
        return $"landing:{host}";
    }

    public static string ToDomainDirectory(string host)
    {
        return host.Replace(":", "_");
    }

    public async Task GenerateDomainLandingAsync(string host, string instanceName, string? markdown)
    {
        var sourceMarkdown = string.IsNullOrWhiteSpace(markdown) ? DefaultMarkdown : markdown;
        var renderedMarkdown = Markdown.ToHtml(sourceMarkdown);

        var template = await GetTemplateAsync();
        var html = template
            .Replace("{{INSTANCE_NAME}}", WebUtility.HtmlEncode(instanceName))
            .Replace("{{INSTANCE_MARKDOWN_HTML}}", IndentLines(renderedMarkdown, "                "));

        var targetDir = Path.Combine(_env.WebRootPath, ToDomainDirectory(host));
        Directory.CreateDirectory(targetDir);

        var outputPath = Path.Combine(targetDir, "landing.html");
        await File.WriteAllTextAsync(outputPath, html, new UTF8Encoding(false));

        _logger.LogInformation("Generated static landing page for {Host} at {Path}", host, outputPath);
    }

    public Task DeleteDomainLandingAsync(string host)
    {
        var outputPath = Path.Combine(_env.WebRootPath, ToDomainDirectory(host), "landing.html");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
            _logger.LogInformation("Deleted static landing page for {Host} at {Path}", host, outputPath);
        }

        return Task.CompletedTask;
    }

    private async Task<string> GetTemplateAsync()
    {
        var templatePath = Path.Combine(_env.WebRootPath, TemplateFileName);
        if (File.Exists(templatePath))
        {
            return await File.ReadAllTextAsync(templatePath);
        }

        _logger.LogWarning("Landing template not found at {Path}. Falling back to built-in template.", templatePath);
        return DefaultTemplate;
    }

    private static string IndentLines(string content, string indent)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(indent);
            sb.Append(lines[i]);
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }
}
