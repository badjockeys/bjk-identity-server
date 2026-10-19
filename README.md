# bjk-identity-server

Identity server for the Bad Jockeys platform. It signs users in, issues tokens to the web and mobile apps, and hosts the user management API from **BJK.Modules.User** (bjk-modules).

```
.github/workflows/   CI (GitHub Actions only reads workflows from here)
docker-compose.yml   local SQL Server (also holds bjk-core's database and its message queues)
user-secrets.template.json   the settings each developer loads into user secrets
docs/                local-development.md: run it on your machine
pipelines/           scripts the workflows call (end-to-end smoke test)
src/
  BJK.Idsrv.Host/
    Program.cs       wires up Identity, IdentityServer and the modules
    Config.cs        scopes and clients
    Data/            BjkIdentityDbContext and migrations
    Services/        IIdentityService implementation for the User module; the profile service (age_verified,
                     minor; no tokens without a date of birth); AgeAssurance/: the YuNet liveness check
tests/
  BJK.Idsrv.Host.Tests/   unit tests (liveness check, date of birth input, age claims) and, when the model is
                          available, tests against real YuNet
```

## What runs inside

| Part | Library | SQL schema |
|---|---|---|
| Sign-in accounts and passwords | ASP.NET Core Identity | `auth` |
| Sign-in pages: sign in/up (date of birth, 13+), two-step (authenticator app, email codes, recovery codes), Google/Apple/Facebook, forgot password, reactivate, account settings, verify your age for sensitive posts | Razor Pages in this host | |
| Age policy (19 Oct 2026): minimum age 13 by declared date of birth (stored on the account; under 13 refused and not kept); `minor` = `"true"` in tokens for under-18s, worked out at every token issue and refresh | BJK.Modules.User + this host | `users` |
| Age check (18+, on demand, to view sensitive posts): head-turn selfie with liveness check and age estimate, or photo ID; `age_verified` claim. Not offered to under-18s by date of birth | BJK.Modules.User + YuNet (ONNX Runtime) in this host | `users` (outcome only) |
| Data Protection keys (cookies, antiforgery, two-step tokens), shared by all instances | ASP.NET Core Data Protection | `auth` |
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
| `Email:Provider` | `Log` (default): emails aren't sent; in Development they're written to the log. `SendGrid`: sent through SendGrid (needs `Email:SendGrid:ApiKey`, secret). |
| `Email:LogoUrl`, `Email:ReplyToAddress` | Optional: PNG logo at the top of emails, and a reply-to address. |
| `Email:FromAddress`, `Email:PasswordResetUrl` | Sender address, and the web app page that reset links point to. |
| `ConnectionStrings:Messaging`, `Messaging:ConnectionStringName` (`Messaging`), `Messaging:CreateTables` (`false`) | Required. User lifecycle events (deactivated, reactivated, deleted) are saved in `users.Outbox` with the account change, then published to the `bjk.users` topic: a row in bjk-core's queue table `[messaging].[bjk-core.user-events]` in the app database (`badjockeys`). The app won't start without it. In Azure this connection uses its own database user in `badjockeys` with `SELECT` on `messaging.subscriptions` and `INSERT` on the `messaging` schema only, so the identity server can queue events but can't read or change bjk-core's data (setup script in the bjk-modules README, "Messaging"). Events wait in the outbox until bjk-core has started once and subscribed. |
| `Users:DeactivationGracePeriod`, `Users:RetentionCheckInterval` | Defaults: 90 days before a deactivated account is deleted; check hourly. |
| `Clients:Web`, `Clients:Mobile`, `Clients:Backoffice` | Each app's redirect addresses (and secret for web and back-office). A client only exists when its `RedirectUris` are set. |
| `Admin:BootstrapEmails` | Accounts made Admin at startup (must exist with a confirmed email). For appointing the first admin. |
| `Authentication:Google`, `:Apple`, `:Facebook` | Social sign-in keys. Each provider is switched on only when its keys are set. |
| `Email:PublicOrigin` | This server's public address, for links in emails. |
| `Captcha:TurnstileSiteKey` | Turnstile site key for the widget on the sign-up, forgot password and reactivate pages. |
| `SignUp:AllowedCountries`, `SignUp:TrustCloudflareCountryHeader`, `SignUp:DevelopmentCountry` | New accounts are UK-only at launch. The country comes from Cloudflare's `CF-IPCountry` header. Defaults: `["GB"]`; `false`, which **must be `true` outside Development** (the app won't start otherwise); none. Outside the list, or with no header, sign-up shows "only open for new accounts in the UK for now" and the API returns 403. Viewing and signing in to existing accounts work from anywhere. Only the country code and outcome are logged, never the IP. |
| `AgeAssurance:*` | Providers and rules for the on-demand age check (sensitive posts only; sign-up has no selfie). Locally, the `Development` providers (from the template) pretend to check and answer a set age. **Outside Development the app won't start with them**: set `AgeAssurance:Liveness:Provider` to `YuNet` (plus `AgeAssurance:Liveness:YuNet:ModelPath`, and ffmpeg in the image), `AgeAssurance:Estimator:Provider` to `Http` (plus `Endpoint` and `ApiKey`, secret), and a real `AgeAssurance:Document:Provider`. See "Age check" in docs/local-development.md and the bjk-modules README. |
| `Support:Email` | Default `support@badjockeys.com`; checked at startup. The "Contact support" link after a failed age check opens an email to it, with the attempt's reference (`AGE-XXXXXX`) in the subject. It's a mailto link for now; a ticketing system (e.g. Zammad, Freshdesk or Help Scout) replaces it once email passes about 50 tickets a week. |
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
