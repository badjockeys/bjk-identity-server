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
| Data Protection keys (cookies, antiforgery, two-step tokens, stored two-step secrets), shared by all instances; encrypted with a certificate in deployed environments | ASP.NET Core Data Protection | `auth` |
| Two-step secrets (authenticator keys and recovery codes) encrypted at rest | `ProtectedUserStore` in this host | `auth.AspNetUserTokens` |
| Tokens, signing keys, sessions | Duende IdentityServer 8 | `idsrv` |
| Account status, deactivation and retention, management API (`/api/users`), outbox of user events | BJK.Modules.User | `users` |

All three share one Azure SQL database (`idsrv`). Each part has its own schema and migrations history. Migrations are applied when the app starts, and any migration that would delete data is refused (see `docs/local-development.md`).

## Configuration

Locally everything comes from your user secrets (`user-secrets.template.json`, loaded by the setup) and no supplier account is needed: email goes to the log, and the age check, CAPTCHA and social sign-in use stand-ins or are off. The table below is every setting; [Going live](#going-live) lists what a deployed environment must change.

| Key | Notes |
|---|---|
| `ConnectionStrings:IdentityDatabase` | Required. Deployed environments must use an encrypted connection: see [Encryption](#encryption). |
| `DataProtection:Certificate` | **Required outside Development** (the app won't start without it, or if it can't be read): the certificate that encrypts the Data Protection keys, a PFX file with its private key as base64. Secret. Empty locally (keys stored unencrypted, with a warning in the log). See [Encryption](#encryption). |
| `DataProtection:CertificatePassword`, `DataProtection:PreviousCertificates` | Secrets. The PFX password, and certificates used before a rotation (so older keys can still be read). |
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

Locally these come from .NET user secrets: load them from `user-secrets.template.json`. When deployed they come from the container's settings, with secrets as container secrets. The appsettings files hold no environment settings, and no secrets are stored in the repo.

### Going live

**Outside Development the identity server refuses to start until every setting below has a live value**, and lists every problem at once (any `<...>` placeholder fails too). `dotnet BJK.Idsrv.Host.dll --check-settings` runs the same checks and exits 0 or 1 without touching the database; the deploy pipeline runs it with each environment's settings before rolling out. Test servers need real values too (SendGrid's `SandboxMode` for email).

Secrets are container secrets; everything else is a container setting (Terraform). Nothing needs a cloud-specific service. Nested keys become environment variables with `__`, e.g. `Email__SendGrid__ApiKey`.

| Setting | Live value | Still needed |
|---|---|---|
| `ConnectionStrings:IdentityDatabase` (secret) | Azure SQL `idsrv` with its own SQL login, `Encrypt=True` | The live server |
| `ConnectionStrings:Messaging` (secret) | The `badjockeys` database, with the restricted messaging login | The live server |
| `Duende:LicenseKey` (secret) | The Community Edition key | Apply for it |
| `MediatR:LicenseKey` (secret) | The licence key | Get one |
| `DataProtection:Certificate`, `CertificatePassword` (secrets) | A self-signed certificate made once (see [Encryption](#encryption)) | Make it and keep a safe copy |
| `Email:Provider` | `SendGrid`. **`Log` is the default in every environment**, so without this nothing is sent | A SendGrid account |
| `Email:SendGrid:ApiKey` (secret) | A key with Mail Send only | A SendGrid account |
| `Email:FromAddress`, `Email:ReplyToAddress`, `Email:LogoUrl` | e.g. `no-reply@badjockeys.com` (verified domain), `support@badjockeys.com`, a PNG logo URL | Domain verified in SendGrid |
| `Email:PublicOrigin`, `Email:PasswordResetUrl` | e.g. `https://auth.badjockeys.com`, `https://auth.badjockeys.com/Account/ResetPassword` | The live domain |
| `Captcha:TurnstileSecretKey` (secret), `Captcha:TurnstileSiteKey` | The real Turnstile widget's keys | A Cloudflare account |
| `Clients:Web` | `RedirectUris` e.g. `https://badjockeys.com/api/auth/callback/bjk`, `PostLogoutRedirectUris` `https://badjockeys.com/`, `Secret` (secret, matches the web app's `IDSRV_CLIENT_SECRET`) | The live domain |
| `Clients:Backoffice` | The back office's addresses and secret | The back office (not built) |
| `Authentication:Google`, `:Apple`, `:Facebook` (secrets) | Optional; each stays off until its keys are set | Developer accounts, if wanted |
| `SignUp:TrustCloudflareCountryHeader` | `true` (required outside Development) | Cloudflare in front |
| `AgeAssurance:Liveness:Provider` | `YuNet`, with `YuNet:ModelPath` and ffmpeg in the image | The model in the image |
| `AgeAssurance:Estimator:Provider` | `Http`, with `Http:Name`, `Endpoint` and `ApiKey` (secret) | An age estimation vendor |
| `AgeAssurance:Document:Provider` | A photo-ID provider | **Not built yet**: choose a vendor and add an `IDocumentAgeCheck` |
| `Admin:BootstrapEmails` | The first admin's confirmed email | |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` (secret), `OTEL_EXPORTER_OTLP_PROTOCOL` | Grafana Cloud's OTLP address (https), `Authorization=Basic <instance id:token, base64>`, `http/protobuf`. Locally `launchSettings.json` sends to bjk-core's `otel-lgtm` container (http://localhost:4318) | A Grafana Cloud stack |

## Encryption

**Data Protection keys.** Cookies, antiforgery tokens, two-step tokens and the stored two-step secrets are protected with ASP.NET Core Data Protection. Its keys live in SQL (`auth.DataProtectionKeys`) so every instance shares them.
- **Deployed environments:** each key is encrypted with an X.509 certificate (`DataProtection:Certificate`, a PFX file with its private key as base64, via `ProtectKeysWithCertificate`). The certificate is a container secret, kept apart from the database, so a copy of the database alone can't read the keys. Standard .NET: it works the same on any cloud. The app refuses to start outside Development without it, or if it can't be read.
- **Making the certificate** (once per environment; it doesn't need a certificate authority):
  ```
  openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -subj "/CN=bjk-dataprotection" -keyout dp.key -out dp.crt
  openssl pkcs12 -export -inkey dp.key -in dp.crt -out dp.pfx -passout pass:<a password>
  base64 -i dp.pfx    # the value for DataProtection:Certificate
  ```
  Store the PFX and password somewhere safe outside the cloud (e.g. a password manager), then delete the local files.
- **Don't lose the certificate.** Without it, existing keys can't be read: everyone is signed out, and stored two-step secrets can't be read. It doesn't matter if it expires (Data Protection still uses it).
- **Rotating it:** make a new one, put it in `DataProtection:Certificate`, and move the old one to `DataProtection:PreviousCertificates` (same password) so keys it encrypted can still be read. New keys (made every 90 days) use the new certificate; drop the old one after 90 days.
- **Locally,** the keys are stored unencrypted and the app logs a warning.

**Two-step secrets.** Authenticator app keys and recovery codes in `auth.AspNetUserTokens` are encrypted with Data Protection (`ProtectedUserStore`, stored as `dp1:` plus the payload).
- ASP.NET Identity uses them exactly as before.
- At startup, after the settings checks, the app encrypts any older plain-text values. Each row is only updated if it still holds the value that was read, so this is safe to run on several instances at once. Only the number of rows is logged.
- No schema change is needed (the column is `nvarchar(max)`).

**Database connections.** Deployed connection strings must:
- encrypt the connection and check the server's certificate: `Encrypt=True`, and never `TrustServerCertificate=True`;
- sign in with the app's own SQL login (not an admin login), whose password is a container secret. The whole connection string is a secret.

For example:

```
Server=tcp:<server>.database.windows.net,1433;Database=idsrv;Encrypt=True;User Id=bjk_idsrv;Password=<secret>
```

Each app gets its own login with only the roles it needs. Plain SQL logins keep this the same on any cloud's SQL Server. `TrustServerCertificate=True` appears only in the local Docker and CI connection strings, where SQL Server has a self-signed certificate.

## Local development

Local SQL Server runs in Docker from **bjk-core** (`docker-compose.yml`, Docker group `bad-jockeys`, password in `bjk-core/.env`), alongside the file storage. The quickest start is bjk-core's one-command setup (`setup.sh` on a Mac, `setup.ps1` on Windows), then F5 in the `badjockeys.code-workspace`. To run just this server:

```bash
(cd ../bjk-core && docker compose up -d --wait)                   # SQL Server (and S3Mock) in Docker
dotnet run --project src/BJK.Idsrv.Host --launch-profile https    # https://localhost:5001
```

First-time setup (NuGet feed token, connection string in user secrets, Docker on Apple Silicon) and a walkthrough: [docs/local-development.md](docs/local-development.md).
