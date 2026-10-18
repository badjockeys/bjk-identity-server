# bjk-identity-server

Identity server for the Bad Jockeys platform. It signs users in, issues tokens to the web and mobile apps, and hosts the user management API from **BJK.Modules.User** (bjk-modules).

```
.github/workflows/   CI (GitHub Actions only reads workflows from here)
docker-compose.yml   local SQL Server
user-secrets.template.json   the settings each developer loads into user secrets
docs/                local-development.md: run it on your machine
pipelines/           scripts the workflows call (end-to-end smoke test)
src/
  BJK.Idsrv.Host/
    Program.cs       wires up Identity, IdentityServer and the modules
    Config.cs        scopes and clients
    Data/            BjkIdentityDbContext and migrations
    Services/        IIdentityService implementation for the User module
```

## What runs inside

| Part | Library | SQL schema |
|---|---|---|
| Sign-in accounts and passwords | ASP.NET Core Identity | `auth` |
| Tokens, signing keys, sessions | Duende IdentityServer 8 | `idsrv` |
| Account status, deactivation and retention, management API (`/api/users`), outbox of user events | BJK.Modules.User | `users` |

All three share one Azure SQL database (`idsrv`). Each part has its own schema and migrations history. Migrations are applied when the app starts, and any migration that would delete data is refused (see `docs/local-development.md`).

## Configuration

| Key | Notes |
|---|---|
| `ConnectionStrings:IdentityDatabase` | Required. |
| `Duende:LicenseKey` | Not needed for development or testing; required in production. The free Community Edition covers companies under $1M revenue. |
| `MediatR:LicenseKey` | MediatR 13+ licence key; free Community licence under $5M revenue. |
| `Database:AllowDestructiveMigrations` | Default `false`. |
| `Email:Provider` | `Log` (default): emails aren't sent; in Development they're written to the log. `SendGrid` is planned. |
| `Email:FromAddress`, `Email:PasswordResetUrl` | Sender address, and the web app page that reset links point to. |
| `Messaging:ConnectionUri` | RabbitMQ. Required. User lifecycle events (deactivated, reactivated, deleted) are published to the `bjk.users` exchange for bjk-core. |
| `Users:DeactivationGracePeriod`, `Users:RetentionCheckInterval` | Defaults: 90 days before a deactivated account is deleted; check hourly. |
| `Captcha:TurnstileSecretKey` | Cloudflare Turnstile secret. Required. Development uses Cloudflare's test key; register, forgot password and reactivate need an `X-Captcha-Token` header. |

Locally these come from .NET user secrets: load them from `user-secrets.template.json`. In Azure they come from Key Vault and App Service settings. The appsettings files hold no environment settings, and no secrets are stored in the repo.

## Local development

```bash
cp .env.example .env                                              # first time: choose a SQL password,
                                                                  # then load user-secrets.template.json (see docs)
docker compose up -d                                              # SQL Server in Docker
dotnet run --project src/BJK.Idsrv.Host --launch-profile https    # https://localhost:5001
```

First-time setup (NuGet feed token, connection string in user secrets, Docker on Apple Silicon) and a walkthrough: [docs/local-development.md](docs/local-development.md).
