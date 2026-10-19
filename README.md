# bjk-identity-server

Identity server for the Bad Jockeys platform. It signs users in, issues tokens to the web and mobile apps, and hosts the user management API from **BJK.Modules.User** (bjk-modules).

```
.github/workflows/   CI (GitHub Actions only reads workflows from here)
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
| Data Protection keys (cookies, antiforgery, two-step tokens, stored two-step secrets), shared by all instances; encrypted with an Azure Key Vault key in deployed environments | ASP.NET Core Data Protection + Azure Key Vault | `auth` |
| Two-step secrets (authenticator keys and recovery codes) encrypted at rest | `ProtectedUserStore` in this host | `auth.AspNetUserTokens` |
| Tokens, signing keys, sessions | Duende IdentityServer 8 | `idsrv` |
| Account status, deactivation and retention, management API (`/api/users`), outbox of user events | BJK.Modules.User | `users` |

All three share one Azure SQL database (`idsrv`). Each part has its own schema and migrations history. Migrations are applied when the app starts, and any migration that would delete data is refused (see `docs/local-development.md`).

## Configuration

| Key | Notes |
|---|---|
| `ConnectionStrings:IdentityDatabase` | Required. Deployed environments must use an encrypted connection and the managed identity: see [Encryption](#encryption). |
| `DataProtection:KeyVaultKeyId` | **Required outside Development** (the app won't start without it): the Azure Key Vault key that encrypts the Data Protection keys, e.g. `https://<vault>.vault.azure.net/keys/dataprotection`. Empty locally (keys stored unencrypted, with a warning in the log). |
| `DataProtection:ManagedIdentityClientId` | Optional: client id of a user-assigned managed identity for Key Vault. Empty uses the system-assigned identity (DefaultAzureCredential). |
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

## Encryption

**Data Protection keys.** Cookies, antiforgery tokens, two-step tokens and the stored two-step secrets are protected with ASP.NET Core Data Protection. Its keys live in SQL (`auth.DataProtectionKeys`) so every instance shares them.
- **Deployed environments:** each key is wrapped with an Azure Key Vault key (`DataProtection:KeyVaultKeyId`, via `ProtectKeysWithAzureKeyVault`) using the app's managed identity (DefaultAzureCredential). A copy of the database alone can't unwrap them. The app refuses to start outside Development without the setting.
- **What the managed identity needs on that key:** get, wrap key and unwrap key (Key Vault Crypto User, or an access policy with those permissions).
- **Don't delete or disable the Key Vault key** (keep soft delete and purge protection on). Without it, existing keys can't be unwrapped: everyone is signed out, and stored two-step secrets can't be read. Rotating the key in Key Vault is fine: old versions stay usable.
- **Locally,** the keys are stored unencrypted and the app logs a warning.

**Two-step secrets.** Authenticator app keys and recovery codes in `auth.AspNetUserTokens` are encrypted with Data Protection (`ProtectedUserStore`, stored as `dp1:` plus the payload).
- ASP.NET Identity uses them exactly as before.
- At startup, after the settings checks, the app encrypts any older plain-text values. Each row is only updated if it still holds the value that was read, so this is safe to run on several instances at once. Only the number of rows is logged.
- No schema change is needed (the column is `nvarchar(max)`).

**Database connections.** Deployed connection strings must:
- encrypt the connection and check the server's certificate: `Encrypt=True`, and never `TrustServerCertificate=True`;
- sign in as the app's managed identity: `Authentication=Active Directory Managed Identity`, with no user name or password. For a user-assigned identity, add `User Id=<its client id>`.

For example:

```
Server=tcp:<server>.database.windows.net,1433;Database=idsrv;Encrypt=True;Authentication=Active Directory Managed Identity
```

The managed identity is created as a database user (`CREATE USER [<app name>] FROM EXTERNAL PROVIDER`) with the roles the app needs. `TrustServerCertificate=True` appears only in the local Docker and CI connection strings, where SQL Server has a self-signed certificate.

## Local development

Local SQL Server runs in Docker from **bjk-core** (`docker-compose.yml`, Docker group `bad-jockeys`, password in `bjk-core/.env`), alongside the file storage. The quickest start is bjk-core's one-command setup (`setup.sh` on a Mac, `setup.ps1` on Windows), then F5 in the `badjockeys.code-workspace`. To run just this server:

```bash
(cd ../bjk-core && docker compose up -d --wait)                   # SQL Server (and S3Mock) in Docker
dotnet run --project src/BJK.Idsrv.Host --launch-profile https    # https://localhost:5001
```

First-time setup (NuGet feed token, connection string in user secrets, Docker on Apple Silicon) and a walkthrough: [docs/local-development.md](docs/local-development.md).
