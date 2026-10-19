using BJK.Idsrv.Host;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.AgeAssurance;
using BJK.Idsrv.Host.Services.Captcha;
using BJK.Idsrv.Host.Services.SignUp;
using BJK.Modules.Email;
using BJK.Modules.Shared;
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
        + "(see docs/local-development.md); in Azure it comes from the App Service settings.");
var hostAssembly = typeof(Program).Assembly.GetName().Name;

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
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// Keys for cookies, antiforgery and two-factor tokens, shared by every instance through the database.
builder.Services.AddDataProtection()
    .SetApplicationName("bjk-identity-server")
    .PersistKeysToDbContext<BjkIdentityDbContext>();

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
    // Adds age_verified=true to tokens once the account has passed the age check.
    .AddProfileService<AgeAwareProfileService>();
builder.Services.AddTransient<DefaultClaimsService>();
builder.Services.AddTransient<IClaimsService, AgeVerifiedClaimsService>();

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
// Age check: the open-source YuNet liveness checker (AgeAssurance:Liveness:Provider=YuNet); the User module adds the
// rest and refuses Development providers outside Development.
builder.Services.AddYuNetLiveness(builder.Configuration);
builder.Services.Configure<AdminBootstrapOptions>(builder.Configuration.GetSection(AdminBootstrapOptions.SectionName));

// Sign-in, registration and account pages (/Account, /Manage).
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Manage");
    options.Conventions.AuthorizePage("/Account/CheckAge");
});
builder.Services.AddBjkModules(builder.Configuration, new UserModule(ConnectionStringName), new EmailModule());

builder.Services.AddHealthChecks();

var app = builder.Build();

// Create the database if needed and apply schema changes. Never drops data (see DatabaseMigrator).
await app.Services.MigrateDatabaseAsync<BjkIdentityDbContext>();
await app.Services.MigrateDatabaseAsync<PersistedGrantDbContext>();
await app.Services.InitialiseBjkModulesAsync();
await IdentitySeeder.SeedAsync(app.Services);

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseIdentityServer();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/Manage"));

app.Run();
