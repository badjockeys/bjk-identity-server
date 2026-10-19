using System.Security.Claims;
using BJK.Idsrv.Host.Data;
using BJK.Modules.User.Contracts;
using BJK.Modules.User.Domain;
using Duende.IdentityServer.AspNetIdentity;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Validation;
using Microsoft.AspNetCore.Identity;

namespace BJK.Idsrv.Host.Services;

/// <summary>
/// The usual ASP.NET Identity profile claims, plus the age claims (access tokens, userinfo, and ID tokens that carry
/// profile claims): <c>age_verified</c> = <c>"true"</c> once the account has passed the age check (18+, for
/// sensitive posts), and <c>minor</c> = <c>"true"</c> while the declared date of birth says the user is under 18.
/// Both are worked out afresh every time, so <c>minor</c> lapses on the 18th birthday. Without them the claims are
/// left out entirely.
/// <para>
/// An account is only "active" (can get tokens at all) once it has a date of birth on record. Every new account
/// does; older ones created before dates of birth were asked for don't, so the authorize endpoint sends the browser
/// back to the sign-in pages, which ask for it, and the token endpoint refuses (invalid_grant) until then.
/// </para>
/// </summary>
public sealed class AgeAwareProfileService(
    UserManager<BjkIdentityUser> userManager,
    IUserClaimsPrincipalFactory<BjkIdentityUser> claimsFactory,
    ILogger<ProfileService<BjkIdentityUser>> logger,
    IAccountAgeLookup ages,
    TimeProvider clock)
    : ProfileService<BjkIdentityUser>(userManager, claimsFactory, logger)
{
    public override async Task GetProfileDataAsync(ProfileDataRequestContext context, CancellationToken ct)
    {
        await base.GetProfileDataAsync(context, ct);
        AgeClaims.AddTo(context.IssuedClaims, await AgeClaims.LookUpAsync(context.Subject, ages, ct), AgeRules.Today(clock));
    }

    public override async Task IsActiveAsync(IsActiveContext context, CancellationToken ct)
    {
        await base.IsActiveAsync(context, ct);
        if (context.IsActive)
        {
            var facts = await AgeClaims.LookUpAsync(context.Subject, ages, ct);
            context.IsActive = facts is { IsSignUpComplete: true };
        }
    }
}

/// <summary>
/// Puts the age claims in ID tokens too. IdentityServer normally keeps profile claims out of an ID token issued
/// alongside an access token, so they're added here; everything else is IdentityServer's default.
/// </summary>
public sealed class AgeClaimsService(DefaultClaimsService inner, IAccountAgeLookup ages, TimeProvider clock) : IClaimsService
{
    public async Task<IReadOnlyCollection<Claim>> GetIdentityTokenClaimsAsync(
        ClaimsPrincipal subject,
        ResourceValidationResult resources,
        bool includeAllIdentityClaims,
        ValidatedRequest request,
        CancellationToken ct)
    {
        var claims = (await inner.GetIdentityTokenClaimsAsync(subject, resources, includeAllIdentityClaims, request, ct)).ToList();
        AgeClaims.AddTo(claims, await AgeClaims.LookUpAsync(subject, ages, ct), AgeRules.Today(clock));
        return claims;
    }

    public Task<IReadOnlyCollection<Claim>> GetAccessTokenClaimsAsync(
        ClaimsPrincipal subject,
        ResourceValidationResult resources,
        ValidatedRequest request,
        CancellationToken ct) =>
        inner.GetAccessTokenClaimsAsync(subject, resources, request, ct);
}

internal static class AgeClaims
{
    public static async Task<AccountAgeFacts?> LookUpAsync(ClaimsPrincipal? subject, IAccountAgeLookup lookup, CancellationToken cancellationToken)
    {
        var sub = subject?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var userId) ? await lookup.GetAsync(userId, cancellationToken) : null;
    }

    /// <summary>Adds age_verified and minor ("true") when they apply. Never adds "false" values.</summary>
    public static void AddTo(ICollection<Claim> claims, AccountAgeFacts? facts, DateOnly today)
    {
        if (facts is null)
        {
            return;
        }

        if (facts.AgeVerified)
        {
            Add(claims, AgeVerificationClaims.AgeVerified);
        }

        if (facts.IsMinorOn(today))
        {
            Add(claims, AgeVerificationClaims.Minor);
        }
    }

    private static void Add(ICollection<Claim> claims, string type)
    {
        if (!claims.Any(c => c.Type == type))
        {
            claims.Add(new Claim(type, AgeVerificationClaims.Verified));
        }
    }
}
