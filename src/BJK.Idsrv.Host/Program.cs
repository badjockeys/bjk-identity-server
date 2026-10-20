using BJK.Idsrv.Host;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.AgeAssurance;
using BJK.Idsrv.Host.Services.Captcha;
using BJK.Idsrv.Host.Services.Security;
using BJK.Idsrv.Host.Services.SignUp;
using BJK.Idsrv.Host.Services.Support;
using BJK.Modules.Email;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Configuration;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Security;
using BJK.Modules.User;
using BJK.Modules.User.Infrastructure.Services;
using Duende.IdentityServer.EntityFramework.DbContexts;
using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

const string ConnectionStringName = "IdentityDatabase";
const string MigrationsHistoryTable = "__EFMigrationsHistory";

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName)
    ?? throw new InvalidOperationException(
        $"Connection string '{ConnectionStringName}' is not configured. Locally, set it with dotnet user-secrets "
        + "(see docs/local-development.md); when deployed it comes from the container's settings.");
var hostAssembly = typeof(Program).Assembly.GetName().Name;

// User lifecycle events are queued in bjk-core's database (its messaging schema), through a connection of their own.
var messagingConnectionName = builder.Configuration["Messaging:ConnectionStringName"];
if (string.IsNullOrWhiteSpace(messagingConnectionName)
    || string.Equals(messagingConnectionName, ConnectionStringName, StringComparison.OrdinalIgnoreCase)
    || string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(messagingConnectionName)))
{
    throw new InvalidOperationException(
        "Messaging:ConnectionStringName must name the connection string for bjk-core's app database (normally "
        + "'Messaging', with ConnectionStrings:Messaging set), where user lifecycle events are queued for bjk-core. "
        + "Locally, set both with dotnet user-secrets (see docs/local-development.md).");
}

// --- Sign-in accounts: ASP.NET Core Identity (schema "auth") ---
builder.Services.AddDbContext<BjkIdentityDbContext>(options => options.UseSqlServer(
    connectionString,
    sql =>
    {
        sql.MigrationsHistoryTable(MigrationsHistoryTable, BjkIdentityDbContext.Schema);
        sql.EnableRetryOnFailure();
    }));

builder.Services
    .AddIdentity<BjkIdentityUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        // People can sign in before confirming their email; posting and commenting need it (checked in bjk-core).
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<BjkIdentityDbContext>()
    // The authenticator key and recovery codes are encrypted at rest (Data Protection).
    .AddUserStore<ProtectedUserStore>()
    .AddDefaultTokenProviders();
builder.Services.AddSingleton<TwoFactorSecretProtector>();
builder.Services.AddHostedService<TwoFactorSecretsBackfill>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// Keys for cookies, antiforgery, two-factor tokens and the stored two-step secrets, shared by every instance through
// the database and, outside Development, encrypted with a certificate (DataProtection:Certificate).
builder.Services.AddBjkDataProtection(builder.Configuration);

// Social sign-in: each provider is switched on only when its keys are configured.
builder.Services.AddAuthentication().AddSocialSignIn(builder.Configuration);

// --- Token service: Duende IdentityServer ---
builder.Services
    .AddIdentityServer(options =>
    {
        // Not needed for local development or testing; required in production.
        options.LicenseKey = builder.Configuration["Duende:LicenseKey"];
        options.EmitStaticAudienceClaim = true;
        options.Events.RaiseErrorEvents = true;
        options.Events.RaiseFailureEvents = true;
        options.UserInteraction.LoginUrl = "/Account/Login";
        options.UserInteraction.LogoutUrl = "/Account/Logout";
        options.UserInteraction.ErrorUrl = "/Error";
    })
    .AddInMemoryIdentityResources(Config.IdentityResources)
    .AddInMemoryApiScopes(Config.ApiScopes)
    .AddInMemoryApiResources(Config.ApiResources)
    .AddInMemoryClients(Config.Clients(builder.Configuration, builder.Environment).ToList())
    // Signing keys, refresh tokens and sessions live in SQL (schema "idsrv"),
    // so every App Service instance shares them and they survive restarts and redeploys.
    .AddOperationalStore(options =>
    {
        options.DefaultSchema = Config.OperationalSchema;
        options.ConfigureDbContext = db => db.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsAssembly(hostAssembly);
                sql.MigrationsHistoryTable(MigrationsHistoryTable, Config.OperationalSchema);
                sql.EnableRetryOnFailure();
            });
        options.EnableTokenCleanup = true;
    })
    .AddAspNetIdentity<BjkIdentityUser>()
    // Tokens only for accounts with a date of birth on record; adds age_verified and minor.
    .AddProfileService<AgeAwareProfileService>();
builder.Services.AddTransient<DefaultClaimsService>();
builder.Services.AddTransient<IClaimsService, AgeClaimsService>();

// The module APIs hosted here accept this server's own access tokens, validated in-process.
builder.Services.AddAuthentication()
    .AddLocalApi(ModuleAuthentication.Scheme, options => options.ExpectedScope = UserModule.ApiScope);

// --- Modules ---
builder.Services.AddScoped<IIdentityService, AspNetIdentityService>();
builder.Services.Configure<EmailLinkOptions>(builder.Configuration.GetSection(EmailLinkOptions.SectionName));
builder.Services.AddCaptcha(builder.Configuration);
// New accounts are UK-only at launch (Cloudflare's CF-IPCountry header); viewing and signing in work anywhere.
builder.Services.AddSignUpCountryCheck(builder.Configuration);
builder.Services.AddScoped<IPasswordResetSender, EmailPasswordResetSender>();
builder.Services.AddScoped<IEmailConfirmationSender, EmailConfirmationSender>();
builder.Services.AddScoped<LoginFlow>();
// Where people ask for help (Support:Email), e.g. the "Contact support" link after a failed age check.
builder.Services.AddSupport(builder.Configuration);
// The open-source YuNet liveness checker (AgeAssurance:Liveness:Provider=YuNet), used inside the on-demand age check
// against spoofing; the User module adds the rest and refuses Development providers outside Development.
builder.Services.AddYuNetLiveness(builder.Configuration);
builder.Services.Configure<AdminBootstrapOptions>(builder.Configuration.GetSection(AdminBootstrapOptions.SectionName));

// Sign-in, registration and account pages (/Account, /Manage).
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Manage");
    options.Conventions.AuthorizePage("/Account/CheckAge");
    options.Conventions.AuthorizePage("/Account/DateOfBirth");
});
builder.Services.AddBjkModules(builder.Configuration, new UserModule(ConnectionStringName), new EmailModule());

builder.Services.AddHealthChecks();

// Outside Development the identity server refuses to start until every setting has a live value. The modules add
// their own (Email:Provider must be SendGrid, the MediatR licence and so on); these are the host's.
builder.Services.RequireWhenDeployed("Duende:LicenseKey", "the Duende IdentityServer licence key");
builder.Services.RequireWhenDeployed("Email:PublicOrigin", "this server's https address, for links in emails", DeployedSettingChecks.IsHttps);
builder.Services.RequireWhenDeployed("Email:PasswordResetUrl", "the https address of the reset password page", DeployedSettingChecks.IsHttps);
builder.Services.RequireWhenDeployed("Clients:Web:RedirectUris:0", "the web app's https sign-in callback", DeployedSettingChecks.IsHttps);
builder.Services.RequireWhenDeployed("Clients:Web:Secret", "the web app's client secret");
builder.Services.RequireWhenDeployed(
    "Captcha:TurnstileSecretKey", "the real Turnstile secret key, not one of Cloudflare's test keys", v => !TurnstileTestKeys.IsTestKey(v));
builder.Services.RequireWhenDeployed(
    "Captcha:TurnstileSiteKey", "the real Turnstile site key, not one of Cloudflare's test keys", v => !TurnstileTestKeys.IsTestKey(v));

var app = builder.Build();

// --check-settings: run every startup settings check and exit (0 when ready, 1 with every problem listed), without
// touching the database. Deploy pipelines run it with the environment's settings before rolling out.
if (SettingsCheck.Requested(args))
{
    return SettingsCheck.Run(app.Services);
}

// Check the settings before touching the database: outside Development a missing setting stops here.
app.Services.GetService<Microsoft.Extensions.Options.IStartupValidator>()?.Validate();

// Create the database if needed and apply schema changes. Never drops data (see DatabaseMigrator).
await app.Services.MigrateDatabaseAsync<BjkIdentityDbContext>();
await app.Services.MigrateDatabaseAsync<PersistedGrantDbContext>();
await app.Services.InitialiseBjkModulesAsync();
app.WarnIfUnprotected();
await IdentitySeeder.SeedAsync(app.Services);

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// The sign-in pages' font (wwwroot/fonts), served from here like the web app's, so no request goes to Google.
app.UseStaticFiles();
app.UseIdentityServer();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/Manage"));

app.Run();
return 0;
