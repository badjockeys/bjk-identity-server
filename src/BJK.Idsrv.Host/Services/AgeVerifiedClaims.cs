using System.Security.Claims;
using BJK.Idsrv.Host.Data;
using BJK.Modules.User.Contracts;
using Duende.IdentityServer.AspNetIdentity;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Validation;
using Microsoft.AspNetCore.Identity;

namespace BJK.Idsrv.Host.Services;

/// <summary>
/// The usual ASP.NET Identity profile claims, plus <c>age_verified</c> = <c>"true"</c> once the account has passed
/// the age check (access tokens, userinfo, and ID tokens that carry profile claims). Without a pass the claim is
/// left out entirely.
/// </summary>
public sealed class AgeAwareProfileService(
    UserManager<BjkIdentityUser> userManager,
    IUserClaimsPrincipalFactory<BjkIdentityUser> claimsFactory,
    ILogger<ProfileService<BjkIdentityUser>> logger,
    IAgeVerificationLookup ageVerification)
    : ProfileService<BjkIdentityUser>(userManager, claimsFactory, logger)
{
    public override async Task GetProfileDataAsync(ProfileDataRequestContext context, CancellationToken ct)
    {
        await base.GetProfileDataAsync(context, ct);
        if (await AgeVerifiedClaims.IsVerifiedAsync(context.Subject, ageVerification, ct))
        {
            AgeVerifiedClaims.AddTo(context.IssuedClaims);
        }
    }
}

/// <summary>
/// Puts <c>age_verified</c> in ID tokens too. IdentityServer normally keeps profile claims out of an ID token issued
/// alongside an access token, so the claim is added here; everything else is IdentityServer's default.
/// </summary>
public sealed class AgeVerifiedClaimsService(DefaultClaimsService inner, IAgeVerificationLookup ageVerification) : IClaimsService
{
    public async Task<IEnumerable<Claim>> GetIdentityTokenClaimsAsync(
        ClaimsPrincipal subject,
        ResourceValidationResult resources,
        bool includeAllIdentityClaims,
        ValidatedRequest request,
        CancellationToken ct)
    {
        var claims = (await inner.GetIdentityTokenClaimsAsync(subject, resources, includeAllIdentityClaims, request, ct)).ToList();
        if (await AgeVerifiedClaims.IsVerifiedAsync(subject, ageVerification, ct))
        {
            AgeVerifiedClaims.AddTo(claims);
        }

        return claims;
    }

    public Task<IEnumerable<Claim>> GetAccessTokenClaimsAsync(
        ClaimsPrincipal subject,
        ResourceValidationResult resources,
        ValidatedRequest request,
        CancellationToken ct) =>
        inner.GetAccessTokenClaimsAsync(subject, resources, request, ct);
}

internal static class AgeVerifiedClaims
{
    public static async Task<bool> IsVerifiedAsync(ClaimsPrincipal? subject, IAgeVerificationLookup lookup, CancellationToken cancellationToken)
    {
        var sub = subject?.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var userId) && await lookup.IsAgeVerifiedAsync(userId, cancellationToken);
    }

    public static void AddTo(ICollection<Claim> claims)
    {
        if (!claims.Any(c => c.Type == AgeVerificationClaims.AgeVerified))
        {
            claims.Add(new Claim(AgeVerificationClaims.AgeVerified, AgeVerificationClaims.Verified));
        }
    }
}
