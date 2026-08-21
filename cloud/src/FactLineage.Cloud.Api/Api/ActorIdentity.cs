using System.Security.Claims;
using FactLineage.Cloud.Api.Domain;

namespace FactLineage.Cloud.Api.Api;

public static class ActorIdentity
{
    private static readonly string[] TenantClaimTypes = ["tid", "http://schemas.microsoft.com/identity/claims/tenantid"];
    private static readonly string[] SubjectClaimTypes = ["oid", "http://schemas.microsoft.com/identity/claims/objectidentifier", "sub", ClaimTypes.NameIdentifier];

    public static string FromClaims(ClaimsPrincipal? principal)
    {
        var tenantId = FindFirst(principal, TenantClaimTypes);
        var subjectId = FindFirst(principal, SubjectClaimTypes);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(subjectId))
        {
            throw new DomainException("ACTOR_IDENTITY_REQUIRED", "The authenticated token must contain stable tenant and actor claims.");
        }

        return $"{tenantId}:{subjectId}";
    }

    private static string? FindFirst(ClaimsPrincipal? principal, IReadOnlyList<string> claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal?.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}