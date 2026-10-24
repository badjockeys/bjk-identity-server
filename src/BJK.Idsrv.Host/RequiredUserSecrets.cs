using System.Reflection;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace BJK.Idsrv.Host;

/// <summary>
/// Makes this app's .NET user secrets file mandatory when <c>BJK_REQUIRE_USER_SECRETS=true</c>. bjk-core's
/// docker-compose.yml sets it for the released images it runs on a developer's machine (the <c>release</c> profile),
/// which mount the developer's user secrets folder, so a missing file or folder stops the app at once instead of it
/// quietly running on stand-ins. Nothing else sets it: F5, CI and deployed environments are unaffected.
/// </summary>
/// <remarks>
/// The path is worked out the way .NET itself finds user secrets: the <c>UserSecretsId</c> compiled into this
/// assembly (<c>bjk-identity-server</c>, from the project file) under <c>$HOME/.microsoft/usersecrets</c>
/// (<c>%APPDATA%\Microsoft\UserSecrets</c> on Windows). In the image, <c>$HOME</c> is <c>/home/app</c>.
/// </remarks>
internal static class RequiredUserSecrets
{
    /// <summary>The environment variable that turns the check on.</summary>
    public const string Switch = "BJK_REQUIRE_USER_SECRETS";

    /// <summary>Exits with code 1, saying why, if the check is on and the user secrets won't be read.</summary>
    public static void Check(IHostEnvironment environment)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(Switch), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var id = Assembly.GetEntryAssembly()?.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
        if (id is null)
        {
            Stop("this app has no UserSecretsId, so it has no user secrets to read.");
        }

        var path = PathHelper.GetSecretsPathFromSecretsId(id);
        if (!File.Exists(path))
        {
            Stop($"there's no user secrets file at {path}. Run the setup (scripts/setup in bjk-core), which creates " +
                "it, and check BJK_USER_SECRETS_DIR in bjk-core/.env points at your user secrets folder.");
        }

        if (!environment.IsDevelopment())
        {
            Stop($"user secrets are only read in Development, but ASPNETCORE_ENVIRONMENT is '{environment.EnvironmentName}'.");
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Stop(string reason)
    {
        Console.Error.WriteLine($"{Switch} is true, but {reason}");
        Environment.Exit(1);
    }
}
