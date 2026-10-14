using BJK.Idsrv.Host;
using BJK.Idsrv.Host.Data;
using BJK.Idsrv.Host.Services;
using BJK.Idsrv.Host.Services.Captcha;
using BJK.Idsrv.Host.Services.Email;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Security;
using BJK.Modules.User;
using BJK.Modules.User.Infrastructure.Services;
using Duende.IdentityServer.EntityFramework.DbContexts;
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
    })
    .AddEntityFrameworkStores<BjkIdentityDbContext>()
    .AddDefaultTokenProviders();

// --- Token service: Duende IdentityServer ---
builder.Services
    .AddIdentityServer(options =>
    {
        // Not needed for local development or testing; required in production.
        options.LicenseKey = builder.Configuration["Duende:LicenseKey"];
        options.EmitStaticAudienceClaim = true;
        options.Events.RaiseErrorEvents = true;
        options.Events.RaiseFailureEvents = true;
    })
    .AddInMemoryIdentityResources(Config.IdentityResources)
    .AddInMemoryApiScopes(Config.ApiScopes)
    .AddInMemoryClients(Config.Clients(builder.Environment))
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
    .AddAspNetIdentity<BjkIdentityUser>();

// The module APIs hosted here accept this server's own access tokens, validated in-process.
builder.Services.AddAuthentication()
    .AddLocalApi(ModuleAuthentication.Scheme, options => options.ExpectedScope = UserModule.ApiScope);

// --- Modules ---
builder.Services.AddScoped<IIdentityService, AspNetIdentityService>();
builder.Services.AddEmail(builder.Configuration);
builder.Services.AddCaptcha(builder.Configuration);
builder.Services.AddScoped<IPasswordResetSender, EmailPasswordResetSender>();
builder.Services.AddBjkModules(builder.Configuration, new UserModule(ConnectionStringName));

builder.Services.AddHealthChecks();

var app = builder.Build();

// Create the database if needed and apply schema changes. Never drops data (see DatabaseMigrator).
await app.Services.MigrateDatabaseAsync<BjkIdentityDbContext>();
await app.Services.MigrateDatabaseAsync<PersistedGrantDbContext>();
await app.Services.InitialiseBjkModulesAsync();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseIdentityServer();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/", () => "Bad Jockeys identity server");

app.Run();
