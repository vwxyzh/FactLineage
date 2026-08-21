using System.Security.Claims;
using AiDoc.Cloud.Api.Api;
using AiDoc.Cloud.Api.Domain;

namespace AiDoc.Cloud.Api.Tests;

public sealed class ActorIdentityTests
{
    [Fact]
    public void FromClaims_CombinesTenantAndObjectIdentifier()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tid", "tenant"),
            new Claim("oid", "actor")
        ], "test"));

        Assert.Equal("tenant:actor", ActorIdentity.FromClaims(principal));
    }

    [Fact]
    public void FromClaims_AcceptsMappedEntraClaimNames()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("http://schemas.microsoft.com/identity/claims/tenantid", "tenant"),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "actor")
        ], "test"));

        Assert.Equal("tenant:actor", ActorIdentity.FromClaims(principal));
    }

    [Fact]
    public void FromClaims_RejectsMissingStableIdentity()
    {
        var exception = Assert.Throws<DomainException>(() => ActorIdentity.FromClaims(new ClaimsPrincipal()));

        Assert.Equal("ACTOR_IDENTITY_REQUIRED", exception.Code);
    }
}