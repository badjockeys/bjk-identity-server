# Running the identity server locally

This runs the identity server on your machine against SQL Server Express, so you can register and manage users through the User module and see the rows in the database.

## 1. Prerequisites

- **.NET 10 SDK**: `dotnet --version` should show 10.0.100 or later.
- **SQL Server Express** with the default instance name `SQLEXPRESS`, and Windows authentication enabled (the installer's default).
- **SQL Server Management Studio** (or Azure Data Studio / VS Code's SQL extension) to look at the data.
- A trusted HTTPS dev certificate: `dotnet dev-certs https --trust`

## 2. Access to the Bad Jockeys NuGet feed

The User module comes from the private `badjockeys` package feed on GitHub Packages, so restore needs a GitHub token.

1. On GitHub: **Settings → Developer settings → Personal access tokens → Tokens (classic) → Generate new token (classic)**. Tick only **`read:packages`**. Copy the token.
2. Store it as a user environment variable. NuGet picks it up for the `badjockeys` source in `nuget.config`, and the token stays out of the repo. In PowerShell:

   ```powershell
   [Environment]::SetEnvironmentVariable("NuGetPackageSourceCredentials_badjockeys", "Username=<your GitHub username>;Password=<token>", "User")
   ```

3. Close and reopen your terminal and Visual Studio so they see the variable.

## 3. Database connection

`src/BJK.Idsrv.Host/appsettings.Development.json` points at:

```
Server=.\SQLEXPRESS;Database=BJK_Identity;Trusted_Connection=True;TrustServerCertificate=True
```

If your instance has a different name, don't edit that file. Override the connection string in user secrets instead, which stay outside the repo:

```bash
dotnet user-secrets set "ConnectionStrings:IdentityDatabase" "Server=.\\MYINSTANCE;Database=BJK_Identity;Trusted_Connection=True;TrustServerCertificate=True" --project src/BJK.Idsrv.Host
```

You don't need to create the database. The app creates `BJK_Identity` on first start.

## 4. Run it

```bash
dotnet run --project src/BJK.Idsrv.Host --launch-profile https
```

Or open `BJK.Idsrv.slnx` in Visual Studio and press F5. The server listens on **https://localhost:5001**.

On startup the console shows each schema being created or brought up to date:

```
BjkIdentityDbContext: applying migrations ...
PersistedGrantDbContext: applying migrations ...
UserDbContext: applying migrations ...
```

On later starts each line says `database schema is up to date`.

## 5. Try it

Open `src/BJK.Idsrv.Host/BJK.Idsrv.Host.http` and send the requests from top to bottom. It's supported in Visual Studio, Rider, and VS Code with the REST Client extension.

| Step | What happens | Where to look in SQL |
|---|---|---|
| Register | Sign-in account + profile created | `auth.AspNetUsers`, `users.Users` |
| Sign in (password grant) | Token issued; signing key created on first use | `idsrv.Keys` |
| Update | Display name changed | `users.Users.DisplayName`, `UpdatedAt` |
| List (admin token) | All active users | |
| Forgot password | Reset token written to the app console | |
| Confirm reset | Password changed | `auth.AspNetUsers.PasswordHash` |
| Delete | Profile soft-deleted, sign-in disabled | `users.Users.IsDeleted = 1`, `auth.AspNetUsers.LockoutEnd` |

Handy queries:

```sql
USE BJK_Identity;
SELECT * FROM users.Users;
SELECT Id, Email, LockoutEnd, AccessFailedCount FROM auth.AspNetUsers;
SELECT * FROM users.__EFMigrationsHistory;
```

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
dotnet ef migrations add <Name> --project src/BJK.Idsrv.Host --context BjkIdentityDbContext --output-dir Data/Migrations/Identity
```

The User module's migrations live in bjk-modules.

## Troubleshooting

- **`NU1301` / `401 Unauthorized` on restore**: the NuGet token variable isn't set or has expired (step 2). Restart your terminal after setting it.
- **`A network-related or instance-specific error`**: SQL Server Express isn't running, or the instance name differs (step 3). Check the "SQL Server (SQLEXPRESS)" service.
- **`Login failed for user`**: your Windows account needs permission to create databases. It does by default if you installed SQL Server Express yourself.
- **Startup stops with "pending migrations would remove data"**: see Database changes above.
