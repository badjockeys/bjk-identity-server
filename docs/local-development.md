# Running the identity server locally

This runs the identity server on your machine against SQL Server in Docker. You can register users, deactivate and reactivate them, and see the rows in the database and the events in bjk-core's queue table. It works the same on macOS (including Apple Silicon), Windows and Linux.

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

## 3. Start SQL Server

SQL Server runs in Docker from **bjk-core**, so clone that next to this repo. Its `docker-compose.yml` starts SQL Server and the local file storage together, as one Docker group called `bad-jockeys`. The `sa` password goes in `bjk-core/.env` (git-ignored, so it never reaches GitHub). bjk-core's setup script creates it for you; by hand:

```bash
cd ../bjk-core
cp .env.example .env
# edit .env: BJK_SQL_PASSWORD=<8+ characters with upper case, lower case, a digit and a symbol>
docker compose --profile source up -d --wait      # returns once SQL Server is healthy (about 30 seconds the first time)
```

`--profile source` starts what the apps need when you run them from source (F5): SQL Server, the file storage and the logging containers. bjk-core's other profile, `release`, also runs the published app images, including this server's (see "Run the released build" in bjk-core's docs/local-development.md).

This runs SQL Server 2022 on `localhost,1433`. Both this server's `idsrv` database and bjk-core's `badjockeys` database live there, and `badjockeys` also holds the message queues (schema `messaging`), so there's no separate message broker. The data lives in a Docker volume, so it's still there after `docker compose stop` or a reboot.

| Command (in bjk-core) | Does |
|---|---|
| `docker compose --profile source stop` | Stops it and keeps the data |
| `docker compose --profile source up -d --wait` | Starts it again |
| `docker compose --profile "*" down -v` | Removes the containers of both profiles **and all local data** (databases and stored files), so the next app start builds fresh databases |

> SQL Server used to run from a `docker-compose.yml` in this repo, with the password in this repo's `.env`. bjk-core's scripts remove the old Docker group and carry that password over to `bjk-core/.env`, so your user secrets keep working.

## 4. Your settings: .NET user secrets

No environment settings live in the repo's appsettings files. Locally they come from .NET user secrets, which are stored in your home folder (`~/.microsoft/usersecrets/bjk-identity-server/secrets.json`), never in the repo, and only load in Development.

`user-secrets.template.json` in the repo root lists every setting. Keys and passwords are blank (`""`): supplier keys you'd get yourself (SendGrid, Yoti, Google, Apple, Facebook) stay empty locally, and bjk-core's setup fills in what it knows when it writes your copy straight into the user secrets (the SQL password from `bjk-core/.env` in place of `Password=;`, the liveness model path, Cloudflare's Turnstile test keys). After that it only adds or renames settings, never overwrites yours.

To view or change them, right-click `src/BJK.Idsrv.Host/BJK.Idsrv.Host.csproj` → **Manage User Secrets** (VS Code with Microsoft's C# Dev Kit, or Visual Studio; in Rider, **Tools → .NET User Secrets**), or open the file: `code ~/.microsoft/usersecrets/bjk-identity-server/secrets.json`. Save and restart the identity server. (Before 22 Oct 2026 the setup kept a copy in `user-secrets.json` here and reloaded it every run; the next run moves it in and renames it `user-secrets.json.moved`.)

| Setting | Local value |
|---|---|
| `ConnectionStrings:IdentityDatabase` | The Docker SQL Server, with your password from `bjk-core/.env` |
| `Email:SendGrid:ApiKey` | Empty: emails go to the console instead of being sent (see Email below). |
| `Email:FromAddress` | Placeholder sender |
| `Email:PublicOrigin`, `Email:PasswordResetUrl` | This server's address, used in confirmation and reset links |
| `Captcha:TurnstileSecretKey`, `Captcha:TurnstileSiteKey` | Empty in the template; required even locally, so the setup puts in Cloudflare's public test keys (see CAPTCHA below) |
| `ConnectionStrings:BadJockeysDatabase` | bjk-core's `badjockeys` database in the same Docker SQL Server, where user lifecycle events are queued for bjk-core. Locally `sa`; in Azure a login that may only add rows to the queue tables (see the bjk-modules README, "Messaging"). |
| `Clients:Web`, `Clients:Mobile`, `Clients:Backoffice` | Where each app gets sent back to after sign-in, and the web and back-office secrets. A client exists only if its `RedirectUris` are set. |
| `Admin:BootstrapEmails` | Empty. Add your email once you've registered and confirmed it, to make yourself the first Admin (see Staff roles below) |
| `SignUp:*` | UK-only sign-up. Locally there's no Cloudflare header, so sign-up is allowed (see UK-only sign-up below) |
| `AgeAssurance:*` | The real head-turn check (the setup sets `Liveness:ModelPath`), and stand-ins for Yoti that answer age 30 (`Development`; see Age policy below) |
| `Support:Email` | Where "Contact support" emails go. Default `support@badjockeys.com` |
| `DataProtection:*` | Leave empty locally. The Data Protection keys are then stored unencrypted, and the app logs a warning. Deployed environments encrypt them with a certificate (see Encryption in the README) |
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
| `/Account/Login`, `/Account/Register` | Sign in, sign up with date of birth (plus social buttons once configured) |
| `/Account/TooYoung` | Kind refusal for under-13s (no account created, date of birth not kept) |
| `/Account/DateOfBirth` | Asks accounts created before dates of birth were asked for (they get no tokens until they give it) |
| `/Account/LoginWith2fa`, `/Account/LoginWithRecoveryCode` | Second step: authenticator code, emailed code, or recovery code |
| `/Account/ForgotPassword`, `/Account/ResetPassword` | Forgot password |
| `/Account/ConfirmEmail` | Where the confirmation email's link lands |
| `/Account/Reactivate` | Bring back a deactivated account |
| `/Account/CheckAge?returnUrl=...` | Verify your age to view sensitive posts: selfie video, or photo ID. The web app sends signed-in users here when they tap a sensitive post |
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

### UK-only sign-up

At launch, new accounts can only be created from the UK. Anyone can still watch posts, and existing accounts sign in from anywhere (people travel). The rule covers the sign-up page, a first Google, Apple or Facebook sign-in, and `POST /api/users`.

In the deployed environments Cloudflare adds a `CF-IPCountry` header with the visitor's country, and the identity server always trusts it there (Cloudflare fronts every request). A country outside `SignUp:AllowedCountries` (default `["GB"]`), Cloudflare's "unknown" (`XX`) or Tor (`T1`), or no header at all, means no account:
- the pages show `/Account/SignUpUnavailable` ("Bad Jockeys is only open for new accounts in the UK for now. You can still watch posts.");
- the API returns 403 with that message.

The log records only the country code and the outcome, e.g. `Sign-up country check: FR refused`, never the IP address.

Locally there's no Cloudflare, so there's no header and sign-up is allowed. To see the refusal, pretend to be elsewhere (in your user secrets, then restart, or for this session only):

```bash
dotnet user-secrets set "SignUp:DevelopmentCountry" "FR" --project src/BJK.Idsrv.Host   # refused
dotnet user-secrets remove "SignUp:DevelopmentCountry" --project src/BJK.Idsrv.Host      # allowed again
```

Or send the header yourself (e.g. `CF-IPCountry: FR` in the `.http` file): a header is read locally too.

`SignUp:DevelopmentCountry` is refused outside Development.

### Age policy

Decided 19 Oct 2026. **Minimum age 13.**

**Sign-up** is the date of birth, the Turnstile CAPTCHA and email confirmation; there's no selfie. The sign-up page (and a first Google, Apple or Facebook sign-in) asks for the date of birth as three labelled boxes (day, month, year). Under 13 goes to a kind refusal page (`/Account/TooYoung`): no account is created and the date isn't kept. The API (`POST /api/users`) takes `dateOfBirth` and answers 400 with error code `too_young`. The date of birth is stored on `users.Accounts.DateOfBirth`, is in "Download my data" and is deleted with the account. Accounts created before this change are asked for it at their next sign-in and get no tokens until they give it.

**Under-18s.** Tokens (access token, ID token, userinfo) carry `"minor": "true"` while the date of birth says the user is under 18. It's worked out every time a token is issued or refreshed (the app clients re-read claims on refresh), so it disappears on the 18th birthday. The apps use it for the high-privacy defaults of the ICO Children's Code.

**Verify your age to view sensitive posts** (`/Account/CheckAge?returnUrl=...`). Sensitive posts appear blurred for everyone. When a signed-in user taps one, the web app sends them here, and back to `returnUrl` (a page of one of our apps) afterwards; the next token or refresh carries `age_verified`. If their date of birth says they're under 18, the page explains that sensitive posts are for adults and offers nothing to check. Otherwise:

1. The page explains what's checked and kept, and asks for consent.
2. It records a 4-second selfie video in the browser, prompting the person to look ahead, turn left, then turn right. No sound is recorded.
3. It uploads the video: a liveness check (against spoofing), then the face age estimate.
4. "Use photo ID instead" uploads a photo of an ID.

Posting, commenting and reacting don't need it. `/Manage` shows the status and links to the page.

**Contact support.** Every attempt gets a short reference, such as `AGE-7KQ2MX`, stored with the outcome (`users.Accounts.AgeCheckReference`) and written to the log line that records it. It holds no images or personal details. When the check fails, can't verify the person, or they've used their 3 tries for the day, the page shows the reference. It explains kindly that a person will review it, without promising an outcome. The "Contact support" link opens an email to `Support:Email` (default `support@badjockeys.com`), with the subject "Age check help – AGE-7KQ2MX" and a body naming the account email (the person adds their @handle, which lives in the core profile). Support finds the attempt with `GET /api/admin/users/{id}` (`ageCheck.reference`). This is a mailto link for now; a ticketing system (e.g. Zammad, Freshdesk or Help Scout) replaces it once email passes about 50 tickets a week.

The rules (from the User module):
- Estimated **25 or over** passes.
- **18 to 24** needs photo ID.
- If the estimate is **unclear** or the liveness check fails, the person tries again or uses photo ID.
- **Clearly under 18** fails, with photo ID still offered. Under 18 on photo ID is final until an admin resets it.
- **3 tries** a day.

The video and photo stay in memory and are wiped after the check. Only the outcome is stored, in `users.Accounts` (`AgeCheckStatus`, `AgeCheckMethod`, `AgeCheckResult`, `AgeCheckProvider`, `AgeCheckRecordedAt`).

**Locally** there are no Yoti keys, so stand-ins answer instead:
- the liveness check is the real one (the setup downloads the YuNet model and sets `AgeAssurance:Liveness:ModelPath`); without the model a stand-in accepts any video;
- the age estimate and the photo ID both say 30 (`AgeAssurance:Development`).

So you can test without a vendor. On a laptop the page works on `https://localhost:5001`, since browsers only allow the camera over HTTPS or on localhost.

To try each path, change the answers in `AgeAssurance:Development` in your user secrets (Manage User Secrets) and restart:

```bash
dotnet user-secrets set "AgeAssurance:Development:EstimatedAge" "20" --project src/BJK.Idsrv.Host   # photo ID needed
dotnet user-secrets set "AgeAssurance:Development:EstimatedAge" "15" --project src/BJK.Idsrv.Host   # under 18
dotnet user-secrets set "AgeAssurance:Development:EstimatedAge" "0" --project src/BJK.Idsrv.Host    # can't tell
dotnet user-secrets set "AgeAssurance:Development:DocumentAge" "16" --project src/BJK.Idsrv.Host    # ID says under 18
dotnet user-secrets set "AgeAssurance:Liveness:ModelPath" "" --project src/BJK.Idsrv.Host           # use the liveness stand-in...
dotnet user-secrets set "AgeAssurance:Development:LivenessPasses" "false" --project src/BJK.Idsrv.Host # ...and make it fail
```

To start again, reset the check as an admin (`POST /api/admin/users/{id}/age-check/reset`), or in SQL:

```sql
UPDATE users.Accounts SET AgeCheckStatus = 'NotChecked', AgeCheckMethod = NULL, AgeCheckResult = NULL, AgeCheckAttempts = 0, AgeCheckAttemptsSince = NULL WHERE Id = '<user id>';
```

Then sign in again (or use the `.http` file's steps). The access token, ID token and userinfo carry `"age_verified": "true"` once passed, and nothing before.

To try the under-18 paths, register with a date of birth 15 years ago: the token carries `"minor": "true"` and the age check page explains it's for adults. A date of birth 12 years ago is refused.

**The real liveness check (YuNet)** runs on our servers, locally too:
1. ffmpeg samples frames from the video.
2. OpenCV Zoo's YuNet face detector (MIT licence) runs through ONNX Runtime.
3. The check looks for one face in most frames, turning clearly one way and then the other.

The setup downloads `face_detection_yunet_2023mar.onnx` into `bjk-core/.local/models` and sets `AgeAssurance:Liveness:ModelPath`. By hand:

```bash
mkdir -p ~/bjk-models && curl -L -o ~/bjk-models/face_detection_yunet_2023mar.onnx \
  https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx
dotnet user-secrets set "AgeAssurance:Liveness:ModelPath" "$HOME/bjk-models/face_detection_yunet_2023mar.onnx" --project src/BJK.Idsrv.Host
```

It's a basic check. It stops a photo held up to the camera, but not a replayed video or a mask; Yoti's own anti-spoofing runs on the frame it estimates from. The app won't start if the model path is set and there's no file.

**Yoti** estimates the age when `AgeAssurance:Yoti:SdkId` and `PrivateKey` are set (from the Yoti Hub; `BaseUrl` is their sandbox for DEV and QA). One frame from the start of the video is sent to Yoti's `/age-antispoofing`, never the video. To try Yoti's sandbox locally, put its keys in your user-secrets.json and run the setup. Photo ID through Yoti isn't built yet.

**Outside Development**, the app refuses to start unless every step has a real provider: the liveness model, Yoti's keys, and (still to build) photo ID.

### The API walkthrough

Open `src/BJK.Idsrv.Host/BJK.Idsrv.Host.http` and send the requests from top to bottom. It works in Rider, Visual Studio, and VS Code with the REST Client extension.

| Step | What happens | Where to look |
|---|---|---|
| Register | Sign-in account and account record (with the date of birth) created; under 13 refused | `auth.AspNetUsers`, `users.Accounts.DateOfBirth` |
| Sign in (password grant) | Token issued; signing key created on first use | `idsrv.Keys` |
| My account | Email (from the sign-in account) and status | |
| Download my data | JSON file of what the identity server holds about you | |
| Age check (sensitive posts) | Status, then a selfie upload (any file works with the Development providers); the next token carries `age_verified` | `users.Accounts.AgeCheckStatus` |
| List (admin token) | All accounts, optionally by status | |
| Forgot password | Reset email written to the app console (not sent yet) | |
| Confirm reset | Password changed | `auth.AspNetUsers.PasswordHash` |
| Deactivate | Sign-in locked, refresh tokens revoked, deletion due in 90 days, event published | `users.Accounts.Status`, `DeletionDueAt`; `users.Outbox`; `badjockeys`: `[messaging].[bjk-core.user-events]` |
| Reactivate | Sign-in works again, deletion cancelled | `users.Accounts` |
| Admin delete | Sign-in account and account record deleted now; `Deleted` event published | |

The display name and handle aren't here: they're in the user's profile in bjk-core.

**Seeing the events.** The identity server publishes to the `bjk.users` topic: each event becomes a row in every queue table subscribed to it, which is bjk-core's `[messaging].[bjk-core.user-events]` in the `badjockeys` database. bjk-core creates that table and its subscription when it starts. Until then nothing is lost: the events wait in `users.Outbox` (`PublishedAt` empty, and the log says no queue subscribes yet). Run bjk-core once (see its docs/local-development.md), and the waiting events go out within a second or two. With bjk-core stopped, the rows stay in its queue table, where you can look at them:

```sql
USE badjockeys;
SELECT id, visible, deliverycount, CAST(body AS varchar(max)) AS event FROM [messaging].[bjk-core.user-events];
```

**Trying the 90-day deletion without waiting.** Deactivate an account, then set its deadline in the past and restart the app (the retention job runs at startup and then hourly):

```sql
UPDATE users.Accounts SET DeletionDueAt = DATEADD(minute, -1, SYSDATETIMEOFFSET()) WHERE Id = '<user id>';
```

To look at the data, connect your SQL tool to server `localhost,1433`, user `sa`, the password from `bjk-core/.env`, and tick "trust server certificate". Handy queries:

```sql
USE idsrv;
SELECT * FROM users.Accounts;
SELECT Type, Payload, OccurredAt, PublishedAt FROM users.Outbox ORDER BY OccurredAt;
SELECT Id, Email, LockoutEnd, AccessFailedCount FROM auth.AspNetUsers;
SELECT * FROM users.__EFMigrationsHistory;
```

Without a SQL tool, you can run the same queries straight in the container:

```bash
docker exec -it bad-jockeys-sql-server bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d idsrv -Q "SELECT Id, Status, DeletionDueAt FROM users.Accounts"'
```

### Email

Emails go through the Email module from bjk-modules. Locally there's no SendGrid key, so nothing is sent: in Development each email (including the confirmation link, sign-in code and password reset code) is written to the app console as a warning starting `DEVELOPMENT ONLY - email not sent`. Only the plain-text version is logged.

To send real emails from your machine, for example to see the HTML version in your inbox:

```bash
dotnet user-secrets set "Email:SendGrid:ApiKey" "<a key with the Mail Send permission>" --project src/BJK.Idsrv.Host
dotnet user-secrets set "Email:FromAddress" "<an address verified in SendGrid>" --project src/BJK.Idsrv.Host
```

With a key set, the app won't start if the sender is missing. Set `Email:SendGrid:SandboxMode` to `true` to have SendGrid check requests without delivering them.

### CAPTCHA

Registration (`POST /api/users`) and forgot password (`POST /api/users/password-reset`) need a Cloudflare Turnstile token in the `X-Captcha-Token` header. Without one, or with an invalid one, they return `400`. If Cloudflare can't be reached, they return `503`.

Locally there's no widget, so your user secrets (from the template) hold Cloudflare's public **"always passes"** test secret. With it, Cloudflare accepts any token, and the `.http` file sends the dummy token `XXXX.DUMMY.TOKEN.XXXX`. To see the rejections:

- **No token:** leave the `X-Captcha-Token` header out, and you get `400`.
- **Cloudflare saying no:** switch to Cloudflare's **"always fails"** test secret, restart the app, and every token is refused with `400`. Switch back afterwards:

  ```bash
  dotnet user-secrets set "Captcha:TurnstileSecretKey" "2x0000000000000000000000000000000AA" --project src/BJK.Idsrv.Host   # always fails
  dotnet user-secrets set "Captcha:TurnstileSecretKey" "1x0000000000000000000000000000000AA" --project src/BJK.Idsrv.Host   # always passes
  ```

In the real apps, the Turnstile widget produces a token just before the request. Tokens are single-use and expire after 5 minutes. When deployed, the real secret key is a container secret, `Captcha:TurnstileSecretKey`.

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

Access tokens for the core API have the audience `bjk-api` and carry `role`, `email_verified` and `email` (for report and appeal acknowledgements), plus `age_verified` (`"true"`) once the account has passed the age check and `minor` (`"true"`) while the date of birth says the user is under 18. Claims are re-read on every refresh. Accounts with no date of birth on record (only ones from before 19 Oct 2026) get no tokens until they give it.

## Database changes

Schema changes are applied automatically when the app starts. A migration that would drop a table, column or schema, or delete rows, **stops the app from starting** and lists what it would remove. To apply one deliberately, review it and set `Database:AllowDestructiveMigrations` to `true` for that run only.

To add a migration after changing `BjkIdentityDbContext`:

```bash
dotnet tool restore
ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add <Name> --project src/BJK.Idsrv.Host --context BjkIdentityDbContext --output-dir Data/Migrations/Identity
```

`ASPNETCORE_ENVIRONMENT=Development` makes the EF tools load your user secrets. The User module's migrations live in bjk-modules.

## Troubleshooting

- **`Connection string 'BadJockeysDatabase' is not configured`**: add `ConnectionStrings:BadJockeysDatabase` from the template to your user secrets (step 4). Older files called it `Messaging`: the bjk-core setup renames it.
- **`No queue subscribes to 'bjk.users' yet`** in the log: bjk-core hasn't started against this SQL Server yet. Events wait in `users.Outbox` until it has.
- **Upgrading from an older checkout**: the account tables changed before release. If startup complains about the `users` schema, run `docker compose --profile "*" down -v && docker compose --profile source up -d --wait` in bjk-core to start from empty databases.
- **`NU1301` / `401 Unauthorized` on restore**: the NuGet token variable isn't set in this terminal, or has expired (step 2). Check with `echo $NuGetPackageSourceCredentials_badjockeys`.
- **`Connection string 'IdentityDatabase' is not configured`** or **`Captcha:TurnstileSecretKey is required`**: your user secrets aren't loaded (step 4), or the app isn't running in Development. The `https` launch profile sets Development.
- **`Login failed for user 'sa'`**: the password in your user secret doesn't match `bjk-core/.env`. If you changed `.env` after the container was first created, SQL Server still has the old password: in bjk-core run `docker compose --profile "*" down -v` (this deletes the local data) and `docker compose --profile source up -d --wait`.
- **`A network-related or instance-specific error`**: SQL Server isn't running or isn't healthy yet. Check `docker compose --profile source ps` and `docker compose logs sql` in bjk-core.
- **`docker compose up` says "Set BJK_SQL_PASSWORD in bjk-core/.env"**: create `bjk-core/.env` from its `.env.example` (step 3).
- **The SQL container keeps restarting on an Apple Silicon Mac**: turn on Rosetta emulation in Docker Desktop (step 1), then `docker compose --profile source up -d --wait` in bjk-core again.
- **Port 1433 is already in use**: another SQL Server is running. Stop it, or change the left-hand port in bjk-core's `docker-compose.yml` (e.g. `"14330:1433"`) and use `Server=localhost,14330` in your user secret.
- **Startup stops with "pending migrations would remove data"**: see Database changes above.
