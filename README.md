# FediProfile - The Decentralized Link-in-Bio

Your links, badges, and identity - powered by the Fediverse.  
The open-source, federated alternative to Linktree.

## What is FediProfile?

FediProfile is a free, privacy-respecting link-in-bio tool built on ActivityPub. Your profile page is itself a first-class Fediverse actor — people on Mastodon, Pleroma, Misskey, and any other ActivityPub platform can discover and interact with it directly.

It's designed for two audiences:

**Newcomers** — A clean, fast link-in-bio with no trackers, no ads, and no account lock-in. It also serves as a gateway into the Fediverse: your profile link is an ActivityPub actor, encouraging exploration of open platforms.

**Fediverse power users** — If you juggle multiple accounts (Mastodon, Pixelfed, Loops, a federated blog…), FediProfile gives you a single persona to share. It doesn't post on its own, but it automatically boosts content from your other accounts and has its own ActivityPub inbox — including the ability to receive and display verifiable badges issued through [BadgeFed](https://badgefed.org).

## Features

- **Unlimited Links** — Add as many links as you want, organize them your way.
- **ActivityPub Native** — Your profile is discoverable and followable from any Fediverse instance.
- **Badge Collection** — Receive and showcase verifiable badges from issuers across the Fediverse via [BadgeFed](https://badgefed.org).
- **Login with Mastodon** — No new password required. Authenticate with your existing Mastodon account from any instance.
- **Auto-Boost & Relay** — Consolidate your Fediverse presence by automatically boosting posts from your other accounts.
- **Self-Hostable** — Run your own instance with full control over your data.
- **No Tracking** — Zero analytics sold, zero ads, zero corporate surveillance.

## How It Works

1. **Sign in with Mastodon** — Enter your instance domain and authorize FediProfile.
2. **Pick your username** — Choose a short, memorable slug. Your profile goes live instantly.
3. **Add links & badges** — Drop in your links. Badges arrive automatically from issuers across the Fediverse.

## Getting Started

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- A Mastodon account (on any instance) for authentication

### Clone & Run

```bash
git clone https://github.com/tryvocalcat/fediprofile.git
cd fediprofile/src/FediProfile
dotnet run
```

The app will start at `http://localhost:5099` by default.

### Configuration

Edit `appsettings.json` to configure your instance:

```json
{
  "AdminAuthentication": {
    "MastodonUser": "your-mastodon-username",
    "MastodonDomain": "your-instance.social"
  },
  "Domains": ["localhost", "localhost:5099"],
  "RegistrationOpen": true,
  "InvitationCode": ""
}
```

| Key | Description |
|---|---|
| `AdminAuthentication` | Mastodon account that has admin access to the instance |
| `Domains` | Domains this instance will respond to |
| `RegistrationOpen` | Set to `true` to allow new sign-ups |
| `InvitationCode` | Optional invite code required for registration |

### Self-Hosting in Production

```bash
dotnet publish -c Release -r linux-x64
```

Deploy the output from `bin/Release/net9.0/linux-x64/publish/` behind a reverse proxy (nginx, Caddy, etc.) with HTTPS enabled — ActivityPub federation requires a valid TLS certificate.

## Project Structure

```
src/FediProfile/
├── Components/        # Blazor pages & layouts
├── Controllers/       # ActivityPub inbox endpoints
├── Core/              # ActivityPub & actor helpers
├── Identity/          # Mastodon OAuth integration
├── Models/            # Data models (actors, badges, links, etc.)
├── Services/          # Database, crypto, follow, profile services
├── wwwroot/           # Static assets & generated profiles
├── Program.cs         # App entry point & service registration
└── appsettings.json   # Configuration
```

## Tech Stack

- **Runtime:** .NET 9.0 / ASP.NET Core
- **UI:** Blazor Server
- **Database:** SQLite (zero-config, file-based)
- **Auth:** Mastodon OAuth
- **Protocol:** ActivityPub / ActivityStreams

## Comparison

| Feature | FediProfile | Linktree | Others |
|---|---|---|---|
| Unlimited links | ✓ Free | ✓ Free | Varies |
| Federated identity | ✓ | ✗ | ✗ |
| Badge / credential system | ✓ Built-in | ✗ | ✗ |
| ActivityPub integration | ✓ Native | ✗ | ✗ |
| Self-hostable | ✓ | ✗ | Some |
| Open source | ✓ | ✗ | Some |
| No tracking / analytics sold | ✓ | ✗ | ✗ |
| Price | Free forever | Free / $9+ | Varies |

## Contributing

Contributions are welcome! Here's how to get involved:

1. **Fork** the repository
2. **Create a branch** for your feature or fix: `git checkout -b my-feature`
3. **Commit** your changes: `git commit -m "Add my feature"`
4. **Push** to your fork: `git push origin my-feature`
5. **Open a Pull Request** against `main`

If you find a bug or have an idea, [open an issue](https://github.com/tryvocalcat/fediprofile/issues).

## Support

FediProfile is free and open source. If it's useful to you, consider buying me a coffee to help cover hosting costs and fuel development:

[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support%20FediProfile-ff5e5b?logo=ko-fi&logoColor=white)](https://ko-fi.com/mahopacheco)

## License

Licensed under the [GNU Lesser General Public License v3](LICENSE.md).
