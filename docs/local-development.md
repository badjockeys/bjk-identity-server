# Running the identity server locally

This runs the identity server on your machine against SQL Server in Docker, so you can register and manage users through the User module and see the rows in the database. It works the same on macOS (including Apple Silicon), Windows and Linux.

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

Choose a password for the local SQL Server's `sa` account. It goes in a `.env` file at the repo root, which is git-ignored, so it never reaches GitHub:

```bash
cp .env.example .env
# edit .env: BJK_SQL_PASSWORD=<your password>
# (8+ characters with upper case, lower case, a digit and a symbol)
```

Then start SQL Server:

```bash
docker compose up -d
docker compose ps        # wait until the sql service shows "healthy" (about 30 seconds)
```

This runs SQL Server 2022 on `localhost,1433`. The data lives in a Docker volume, so it's still there after `docker compose stop` or a reboot.

| Command | Does |
|---|---|
| `docker compose stop` | Stops SQL Server and keeps the data |
| `docker compose up -d` | Starts it again |
| `docker compose down -v` | Removes the container **and the data**, so the next app start builds a fresh database |

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
| `Email:FromAddress`, `Email:PasswordResetUrl` | Placeholder sender, and the web app's future reset page |
| `Captcha:TurnstileSecretKey` | Cloudflare's public test key (see CAPTCHA below) |

To change one setting later:

```bash
dotnet user-secrets set "Email:PasswordResetUrl" "http://localhost:3000/reset-password" --project src/BJK.Idsrv.Host
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

Open `src/BJK.Idsrv.Host/BJK.Idsrv.Host.http` and send the requests from top to bottom. It works in Rider, Visual Studio, and VS Code with the REST Client extension.

| Step | What happens | Where to look in SQL |
|---|---|---|
| Register | Sign-in account + profile created | `auth.AspNetUsers`, `users.Users` |
| Sign in (password grant) | Token issued; signing key created on first use | `idsrv.Keys` |
| Update | Display name changed | `users.Users.DisplayName`, `UpdatedAt` |
| List (admin token) | All active users | |
| Forgot password | Reset email written to the app console (not sent yet) | |
| Confirm reset | Password changed | `auth.AspNetUsers.PasswordHash` |
| Delete | Profile soft-deleted, sign-in disabled | `users.Users.IsDeleted = 1`, `auth.AspNetUsers.LockoutEnd` |

To look at the data, connect your SQL tool to server `localhost,1433`, user `sa`, the password from your `.env`, and tick "trust server certificate". Handy queries:

```sql
USE idsrv;
SELECT * FROM users.Users;
SELECT Id, Email, LockoutEnd, AccessFailedCount FROM auth.AspNetUsers;
SELECT * FROM users.__EFMigrationsHistory;
```

Without a SQL tool, you can run the same queries straight in the container:

```bash
docker exec -it bjk-sql bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d idsrv -Q "SELECT Email, DisplayName, IsDeleted FROM users.Users"'
```

### Email

No email is sent yet. `Email:Provider` is `Log`, so in Development each email (including the password reset code and link) is written to the app console as a warning starting `DEVELOPMENT ONLY - email not sent`. SendGrid will replace this; its API key will go in user secrets / Key Vault as `Email:SendGrid:ApiKey`.

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
| `bjk-local-user` / `local-user-secret` | password | `openid profile email bjk.users` | Sign in as a user without a login page |
| `bjk-local-admin` / `local-admin-secret` | client credentials | `bjk.users bjk.users.manage` | Admin calls such as listing users |

The real web and mobile apps will sign in through a login page (authorization code + PKCE). That comes with the web app.

## Database changes

Schema changes are applied automatically when the app starts. A migration that would drop a table, column or schema, or delete rows, **stops the app from starting** and lists what it would remove. To apply one deliberately, review it and set `Database:AllowDestructiveMigrations` to `true` for that run only.

To add a migration after changing `BjkIdentityDbContext`:

```bash
dotnet tool restore
ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add <Name> --project src/BJK.Idsrv.Host --context BjkIdentityDbContext --output-dir Data/Migrations/Identity
```

`ASPNETCORE_ENVIRONMENT=Development` makes the EF tools load your user secrets. The User module's migrations live in bjk-modules.

## Troubleshooting

- **`NU1301` / `401 Unauthorized` on restore**: the NuGet token variable isn't set in this terminal, or has expired (step 2). Check with `echo $NuGetPackageSourceCredentials_badjockeys`.
- **`Connection string 'IdentityDatabase' is not configured`** or **`Captcha:TurnstileSecretKey is required`**: your user secrets aren't loaded (step 4), or the app isn't running in Development. The `https` launch profile sets Development.
- **`Login failed for user 'sa'`**: the password in your user secret doesn't match `.env`. If you changed `.env` after the container was first created, SQL Server still has the old password: run `docker compose down -v` (this deletes the local data) and `docker compose up -d`.
- **`A network-related or instance-specific error`**: SQL Server isn't running or isn't healthy yet. Check `docker compose ps` and `docker compose logs sql`.
- **`docker compose up` says "Set BJK_SQL_PASSWORD in .env"**: create `.env` from `.env.example` (step 3).
- **The SQL container keeps restarting on an Apple Silicon Mac**: turn on Rosetta emulation in Docker Desktop (step 1), then `docker compose up -d` again.
- **Port 1433 is already in use**: another SQL Server is running. Stop it, or change the left-hand port in `docker-compose.yml` (e.g. `"14330:1433"`) and use `Server=localhost,14330` in your user secret.
- **Startup stops with "pending migrations would remove data"**: see Database changes above.
