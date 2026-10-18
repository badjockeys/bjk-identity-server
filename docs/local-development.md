# Running the identity server locally

This runs the identity server on your machine against SQL Server and RabbitMQ in Docker. You can register users, deactivate and reactivate them, and see the rows in the database and the events in RabbitMQ. It works the same on macOS (including Apple Silicon), Windows and Linux.

## 1. Prerequisites

- **.NET 10 SDK**: `dotnet --version` should show 10.0.100 or later.
- **Docker Desktop**. On Apple Silicon Macs, check that **Settings → General → Use Rosetta for x86_64/amd64 emulation on Apple Silicon** is on, because SQL Server only ships as an x86-64 image.
- A way to look at the data: **VS Code** with the **SQL Server (mssql)** extension, or **Rider** / **DataGrip**.
- An editor for the C#: **Rider**, or **VS Code** with the C# Dev Kit extension.
- A trusted HTTPS dev certificate: `dotnet dev-certs https --trust`

## 2. Access to the Bad Jockeys NuGet feed

The User module comes from the private `badjockeys` package feed on GitHub Packages, so restore needs a GitHub token.

1. On GitHub: **Settings → Developer settings → Personal access tokens → Tokens (classic) → Generate new token (classic)**. Tick only **`read:packages`**. Copy the token.
2. Make it available to NuGet as an environment variable. NuGet picks it up for the `badjockeys` source in `nuget.config`, and the token stays out of the repo.

   **macOS / Linux** (zsh): add this line to `~/.zshrc`, then open a new terminal:

   ```bash
   export NuGetPackageSourceCredentials_badjockeys="Username=<your GitHub username>;Password=<token>"
   ```

   **Windows** (PowerShell), then reopen your terminal:

   ```powershell
   [Environment]::SetEnvironmentVariable("NuGetPackageSourceCredentials_badjockeys", "Username=<your GitHub username>;Password=<token>", "User")
   ```

   Editors only see the variable if started from a terminal that has it, or after you log out and back in. On macOS, `open -a Rider` from that terminal is the simplest way.

## 3. Start SQL Server and RabbitMQ

Choose passwords for the local SQL Server's `sa` account and for RabbitMQ. They go in a `.env` file at the repo root. The file is git-ignored, so it never reaches GitHub:

```bash
cp .env.example .env
# edit .env: BJK_SQL_PASSWORD=<8+ characters with upper case, lower case, a digit and a symbol>
#            BJK_RABBITMQ_PASSWORD=<any password>
```

Then start them:

```bash
docker compose up -d
docker compose ps        # wait until both services show "healthy" (about 30 seconds)
```

This runs SQL Server 2022 on `localhost,1433` and RabbitMQ on `localhost:5672`. bjk-core uses the same two containers. The RabbitMQ dashboard is at http://localhost:15672 (user `bjk`, your `.env` password). The data lives in Docker volumes, so it's still there after `docker compose stop` or a reboot.

| Command | Does |
|---|---|
| `docker compose stop` | Stops both and keeps the data |
| `docker compose up -d` | Starts them again |
| `docker compose down -v` | Removes the containers **and the data**, so the next app start builds a fresh database |

## 4. Load your settings into user secrets

No environment settings live in the repo's appsettings files. Locally they come from .NET user secrets, which are stored in your home folder (`~/.microsoft/usersecrets/bjk-identity-server/secrets.json`), never in the repo, and only load in Development.

`user-secrets.template.json` in the repo root lists every setting with its local value. To load it:

```bash
cp user-secrets.template.json user-secrets.json      # user-secrets.json is git-ignored
# edit user-secrets.json: put the password from your .env into the connection string
dotnet user-secrets set --project src/BJK.Idsrv.Host < user-secrets.json
dotnet user-secrets list --project src/BJK.Idsrv.Host    # check they're all there
```

| Setting | Local value |
|---|---|
| `ConnectionStrings:IdentityDatabase` | The Docker SQL Server, with your password from `.env` |
| `Email:Provider` | `Log`: emails go to the console instead of being sent (see Email below). Leave the SendGrid placeholder unless you switch provider. |
| `Email:FromAddress` | Placeholder sender |
| `Email:PublicOrigin`, `Email:PasswordResetUrl` | This server's address, used in confirmation and reset links |
| `Captcha:TurnstileSecretKey`, `Captcha:TurnstileSiteKey` | Cloudflare's public test keys (see CAPTCHA below) |
| `Messaging:ConnectionUri` | The Docker RabbitMQ, with your password from `.env` |
| `Clients:Web`, `Clients:Mobile`, `Clients:Backoffice` | Where each app gets sent back to after sign-in, and the web and back-office secrets. A client exists only if its `RedirectUris` are set. |
| `Admin:BootstrapEmails` | Your email, to make yourself the first Admin (see Staff roles below) |
| `AgeAssurance:*` | The `Development` age check providers, which pretend to check and answer age 30 (see Age check below) |
| `Authentication:Google`, `:Apple`, `:Facebook` | Leave empty until you have keys; each provider's button appears once its keys are set (see Social sign-in) |

To change one setting later:

```bash
dotnet user-secrets set "Admin:BootstrapEmails:0" "you@example.com" --project src/BJK.Idsrv.Host
```

Optional, when you have them:

```bash
dotnet user-secrets set "Duende:LicenseKey" "<key>" --project src/BJK.Idsrv.Host
dotnet user-secrets set "MediatR:LicenseKey" "<key>" --project src/BJK.Idsrv.Host
```

When someone adds a new setting, they add it to `user-secrets.template.json` in the same pull request, so everyone else knows to load it. You don't need to create the database: the app creates `idsrv` on first start.

## 5. Run it

```bash
dotnet run --project src/BJK.Idsrv.Host --launch-profile https
```

Or open `BJK.Idsrv.slnx` in Rider and run the `https` profile. The server listens on **https://localhost:5001**.

On first start the console shows each schema being created:

```
BjkIdentityDbContext: applying migrations ...
PersistedGrantDbContext: applying migrations ...
UserDbContext: applying migrations ...
```

On later starts each line says `database schema is up to date`.

## 6. Try it

### The sign-in pages

Open https://localhost:5001: it takes you to sign in. From there you can:

- **Create an account.** The CAPTCHA widget passes automatically with the test keys. The confirmation email is written to the console; open its link to confirm.
- **Sign in.**
- **Reset a forgotten password.** The email goes to the console.
- **Reactivate a deactivated account.**
- **Manage your account (`/Manage`):** turn on two-step verification with an authenticator app (scan the QR code) or emailed codes, save recovery codes, change your password, link Google, Apple or Facebook, or deactivate.

These are the pages the web app, phone app and back-office send people to; none of them has its own sign-in form.

| Page | |
|---|---|
| `/Account/Login`, `/Account/Register` | Sign in, sign up (plus social buttons once configured) |
| `/Account/LoginWith2fa`, `/Account/LoginWithRecoveryCode` | Second step: authenticator code, emailed code, or recovery code |
| `/Account/ForgotPassword`, `/Account/ResetPassword` | Forgot password |
| `/Account/ConfirmEmail` | Where the confirmation email's link lands |
| `/Account/Reactivate` | Bring back a deactivated account |
| `/Account/CheckAge` | Check your age: selfie video, or photo ID (shown straight after sign-up) |
| `/Manage` | Account overview, two-step, password, social sign-in, deactivate |

### Staff roles

Moderators and Admins must use two-step verification. A staff member without it is sent to set up an authenticator app straight after signing in, then signs in again. Their staff rights (the `/api/admin` endpoints) only count when the session used two-step.

To make yourself the first Admin:
1. Register and confirm your email.
2. Put your email in `Admin:BootstrapEmails` and restart.
3. From then on, admins give or take roles with `PUT /api/admin/users/{id}/roles` (`["Moderator"]`, `["Admin"]` or `[]`), later from the back-office.

### Social sign-in

Each provider switches on when its keys are in your user secrets. Every provider has to know this server's callback address:

| Provider | Where to get keys | Callback address to register |
|---|---|---|
| Google | Google Cloud console → APIs & Services → Credentials → OAuth client (Web application) | `https://localhost:5001/signin-google` |
| Facebook | developers.facebook.com → your app → Facebook Login | `https://localhost:5001/signin-facebook` |
| Apple | developer.apple.com → Certificates, IDs & Profiles: a Services ID (the client id), a Sign in with Apple key (.p8, key id) and your team id. Apple needs a real HTTPS domain, so try it on DEV rather than localhost. | `https://<domain>/signin-apple` |

A first social sign-in creates an account with the provider's email (already confirmed). If an account with that email already exists, the two are never joined automatically; the person signs in with their password and links the provider on `/Manage`.

### Age check

Bad Jockeys is for people aged 18 and over. Straight after sign-up (email or Google/Apple/Facebook), people land on **Check your age** (`/Account/CheckAge`):

1. The page explains what's checked and kept, and asks for consent.
2. It records a 4-second selfie video in the browser, prompting the person to look ahead, turn left, then turn right. No sound is recorded.
3. It uploads the video.
4. "Use photo ID instead" uploads a photo of an ID.

Anyone can **skip** it and still sign in. Their tokens just don't carry `age_verified` until they pass, and the web app prompts them. `/Manage` shows the status and links back to the page.

The rules (from the User module):
- Estimated **25 or over** passes.
- **18 to 24** needs photo ID.
- If the estimate is **unclear** or the liveness check fails, the person tries again or uses photo ID.
- **Clearly under 18** fails, with photo ID still offered. Under 18 on photo ID is final until an admin resets it.
- **3 tries** a day.

The video and photo stay in memory and are wiped after the check. Only the outcome is stored, in `users.Accounts` (`AgeCheckStatus`, `AgeCheckMethod`, `AgeCheckResult`, `AgeCheckProvider`, `AgeCheckRecordedAt`).

**Locally** the template selects the `Development` providers:
- the liveness check accepts any video;
- the age estimate and the photo ID both say 30.

So you can test without a vendor. On a laptop the page works on `https://localhost:5001`, since browsers only allow the camera over HTTPS or on localhost.

To try each path, change the answers and restart:

```bash
dotnet user-secrets set "AgeAssurance:Estimator:Development:Age" "20" --project src/BJK.Idsrv.Host   # photo ID needed
dotnet user-secrets set "AgeAssurance:Estimator:Development:Age" "15" --project src/BJK.Idsrv.Host   # under 18
dotnet user-secrets set "AgeAssurance:Estimator:Development:Age" "0" --project src/BJK.Idsrv.Host    # can't tell
dotnet user-secrets set "AgeAssurance:Document:Development:Age" "16" --project src/BJK.Idsrv.Host    # ID says under 18
dotnet user-secrets set "AgeAssurance:Liveness:Development:Pass" "false" --project src/BJK.Idsrv.Host # liveness fails
```

To start again, reset the check as an admin (`POST /api/admin/users/{id}/age-check/reset`), or in SQL:

```sql
UPDATE users.Accounts SET AgeCheckStatus = 'NotChecked', AgeCheckMethod = NULL, AgeCheckResult = NULL, AgeCheckAttempts = 0, AgeCheckAttemptsSince = NULL WHERE Id = '<user id>';
```

Then sign in again (or use the `.http` file's steps). The access token, ID token and userinfo carry `"age_verified": "true"` once passed, and nothing before.

**Trying the real liveness check (YuNet).** It runs on our servers:
1. ffmpeg samples frames from the video.
2. OpenCV Zoo's YuNet face detector (MIT licence) runs through ONNX Runtime.
3. The check looks for one face in most frames, turning clearly one way and then the other.

To use it locally:

```bash
brew install ffmpeg      # or apt-get install ffmpeg
mkdir -p ~/bjk-models && curl -L -o ~/bjk-models/face_detection_yunet_2023mar.onnx \
  https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx
dotnet user-secrets set "AgeAssurance:Liveness:Provider" "YuNet" --project src/BJK.Idsrv.Host
dotnet user-secrets set "AgeAssurance:Liveness:YuNet:ModelPath" "$HOME/bjk-models/face_detection_yunet_2023mar.onnx" --project src/BJK.Idsrv.Host
```

It's a basic check. It stops a photo held up to the camera, but not a replayed video or a mask. A passive anti-spoofing model (for example MiniFASNet, Apache-2.0) is the next step. The app won't start with `YuNet` selected and no model file.

**A real age estimator** plugs in with `AgeAssurance:Estimator:Provider=Http`. The generic adapter:
- POSTs the video as multipart/form-data (`VideoField`, default `video`);
- sends `ApiKey` in `ApiKeyHeader` (default `Authorization: Bearer …`);
- reads the age from the JSON response at `AgePath` (and optionally a margin of error at `UncertaintyPath`).

Set `AgeAssurance:Estimator:Http:Endpoint` to the vendor's https address. A vendor with a different API gets its own `IAgeEstimator`. No photo-ID vendor is wired up yet; that's a new `IDocumentAgeCheck`.

**Outside Development**, the app refuses to start with any `Development` age check provider.

### The API walkthrough

Open `src/BJK.Idsrv.Host/BJK.Idsrv.Host.http` and send the requests from top to bottom. It works in Rider, Visual Studio, and VS Code with the REST Client extension.

| Step | What happens | Where to look |
|---|---|---|
| Register | Sign-in account and account record created | `auth.AspNetUsers`, `users.Accounts` |
| Sign in (password grant) | Token issued; signing key created on first use | `idsrv.Keys` |
| My account | Email (from the sign-in account) and status | |
| Download my data | JSON file of what the identity server holds about you | |
| Age check | Status, then a selfie upload (any file works with the Development providers); the next token carries `age_verified` | `users.Accounts.AgeCheckStatus` |
| List (admin token) | All accounts, optionally by status | |
| Forgot password | Reset email written to the app console (not sent yet) | |
| Confirm reset | Password changed | `auth.AspNetUsers.PasswordHash` |
| Deactivate | Sign-in locked, refresh tokens revoked, deletion due in 90 days, event published | `users.Accounts.Status`, `DeletionDueAt`; `users.Outbox`; RabbitMQ `bjk.users` exchange |
| Reactivate | Sign-in works again, deletion cancelled | `users.Accounts` |
| Admin delete | Sign-in account and account record deleted now; `Deleted` event published | |

The display name and handle aren't here: they're in the user's profile in bjk-core.

**Seeing the events.** The identity server publishes to the `bjk.users` exchange. With nothing bound to it, RabbitMQ drops the messages. To watch them, either run bjk-core, which creates its `bjk-core.user-events` queue, or bind a queue to the exchange in the dashboard (Queues, Add queue, then Bindings, from `bjk.users`).

**Trying the 90-day deletion without waiting.** Deactivate an account, then set its deadline in the past and restart the app (the retention job runs at startup and then hourly):

```sql
UPDATE users.Accounts SET DeletionDueAt = DATEADD(minute, -1, SYSDATETIMEOFFSET()) WHERE Id = '<user id>';
```

To look at the data, connect your SQL tool to server `localhost,1433`, user `sa`, the password from your `.env`, and tick "trust server certificate". Handy queries:

```sql
USE idsrv;
SELECT * FROM users.Accounts;
SELECT Type, Payload, OccurredAt, PublishedAt FROM users.Outbox ORDER BY OccurredAt;
SELECT Id, Email, LockoutEnd, AccessFailedCount FROM auth.AspNetUsers;
SELECT * FROM users.__EFMigrationsHistory;
```

Without a SQL tool, you can run the same queries straight in the container:

```bash
docker exec -it bjk-sql bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d idsrv -Q "SELECT Id, Status, DeletionDueAt FROM users.Accounts"'
```

### Email

Emails go through the Email module from bjk-modules. Locally `Email:Provider` is `Log`, so nothing is sent: in Development each email (including the confirmation link, sign-in code and password reset code) is written to the app console as a warning starting `DEVELOPMENT ONLY - email not sent`. Only the plain-text version is logged.

To send real emails from your machine, for example to see the HTML version in your inbox:

```bash
dotnet user-secrets set "Email:Provider" "SendGrid" --project src/BJK.Idsrv.Host
dotnet user-secrets set "Email:SendGrid:ApiKey" "<a key with the Mail Send permission>" --project src/BJK.Idsrv.Host
dotnet user-secrets set "Email:FromAddress" "<an address verified in SendGrid>" --project src/BJK.Idsrv.Host
```

The app won't start if `Provider` is `SendGrid` and the key or sender is missing. Set `Email:SendGrid:SandboxMode` to `true` to have SendGrid check requests without delivering them.

### CAPTCHA

Registration (`POST /api/users`) and forgot password (`POST /api/users/password-reset`) need a Cloudflare Turnstile token in the `X-Captcha-Token` header. Without one, or with an invalid one, they return `400`. If Cloudflare can't be reached, they return `503`.

Locally there's no widget, so your user secrets (from the template) hold Cloudflare's public **"always passes"** test secret. With it, Cloudflare accepts any token, and the `.http` file sends the dummy token `XXXX.DUMMY.TOKEN.XXXX`. To see the rejections:

- **No token:** leave the `X-Captcha-Token` header out, and you get `400`.
- **Cloudflare saying no:** switch to Cloudflare's **"always fails"** test secret, restart the app, and every token is refused with `400`. Switch back afterwards:

  ```bash
  dotnet user-secrets set "Captcha:TurnstileSecretKey" "2x0000000000000000000000000000000AA" --project src/BJK.Idsrv.Host   # always fails
  dotnet user-secrets set "Captcha:TurnstileSecretKey" "1x0000000000000000000000000000000AA" --project src/BJK.Idsrv.Host   # always passes
  ```

In the real apps, the Turnstile widget produces a token just before the request. Tokens are single-use and expire after 5 minutes. In Azure the real secret key goes in Key Vault as `Captcha:TurnstileSecretKey`.

### The local test clients

These two clients exist **only when `ASPNETCORE_ENVIRONMENT=Development`**:

| Client | Grant | Scopes | Use |
|---|---|---|---|
| `bjk-local-user` / `local-user-secret` | password | user scopes, plus the `.manage` scopes | Sign in as a user without the login page. Staff rights never work with it, because there's no two-step. |
| `bjk-local-admin` / `local-admin-secret` | client credentials | user and `.manage` scopes | Admin calls such as listing accounts or setting roles |

The real apps use these clients (authorization code + PKCE, refresh tokens), configured under `Clients` in your user secrets:

| Client | Who | Notes |
|---|---|---|
| `bjk-web` | The website (Next.js), signing in on its server | Has a secret; 15-minute access tokens, 30-day sliding refresh tokens |
| `bjk-mobile` | The phone apps, through the system browser | No secret (public client); `badjockeys://auth/callback` |
| `bjk-backoffice` | Moderators and admins | Has a secret; adds the `.manage` scopes; non-staff are turned away at sign-in; 10-minute access tokens, 8-hour sessions |

Access tokens for the core API have the audience `bjk-api` and carry `role`, `email_verified` and `email` (for report and appeal acknowledgements), plus `age_verified` (`"true"`) once the account has passed the age check.

## Database changes

Schema changes are applied automatically when the app starts. A migration that would drop a table, column or schema, or delete rows, **stops the app from starting** and lists what it would remove. To apply one deliberately, review it and set `Database:AllowDestructiveMigrations` to `true` for that run only.

To add a migration after changing `BjkIdentityDbContext`:

```bash
dotnet tool restore
ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add <Name> --project src/BJK.Idsrv.Host --context BjkIdentityDbContext --output-dir Data/Migrations/Identity
```

`ASPNETCORE_ENVIRONMENT=Development` makes the EF tools load your user secrets. The User module's migrations live in bjk-modules.

## Troubleshooting

- **`The ConnectionUri field is required`** (Messaging): add `Messaging:ConnectionUri` to your user secrets (step 4). **`None of the specified endpoints were reachable`**: RabbitMQ isn't running (`docker compose ps`), or the password doesn't match `.env`.
- **Upgrading from an older checkout**: the account tables changed before release. If startup complains about the `users` schema, run `docker compose down -v && docker compose up -d` to start from an empty database.
- **`NU1301` / `401 Unauthorized` on restore**: the NuGet token variable isn't set in this terminal, or has expired (step 2). Check with `echo $NuGetPackageSourceCredentials_badjockeys`.
- **`Connection string 'IdentityDatabase' is not configured`** or **`Captcha:TurnstileSecretKey is required`**: your user secrets aren't loaded (step 4), or the app isn't running in Development. The `https` launch profile sets Development.
- **`Login failed for user 'sa'`**: the password in your user secret doesn't match `.env`. If you changed `.env` after the container was first created, SQL Server still has the old password: run `docker compose down -v` (this deletes the local data) and `docker compose up -d`.
- **`A network-related or instance-specific error`**: SQL Server isn't running or isn't healthy yet. Check `docker compose ps` and `docker compose logs sql`.
- **`docker compose up` says "Set BJK_SQL_PASSWORD in .env"**: create `.env` from `.env.example` (step 3).
- **The SQL container keeps restarting on an Apple Silicon Mac**: turn on Rosetta emulation in Docker Desktop (step 1), then `docker compose up -d` again.
- **Port 1433 is already in use**: another SQL Server is running. Stop it, or change the left-hand port in `docker-compose.yml` (e.g. `"14330:1433"`) and use `Server=localhost,14330` in your user secret.
- **Startup stops with "pending migrations would remove data"**: see Database changes above.
