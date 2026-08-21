using FactLineage.Cloud.Api.Api;
using FactLineage.Cloud.Api.Configuration;

namespace FactLineage.Cloud.Api.Tests;

public sealed class McpDiscoveryDocumentTests
{
    [Fact]
    public void Create_ReturnsPublicEntraAndEndpointConfigurationOnly()
    {
        var options = new CloudOptions
        {
            ManagedIdentityClientId = "11111111-1111-1111-1111-111111111111",
            TenantId = "22222222-2222-2222-2222-222222222222",
            ApiAudience = "api://33333333-3333-3333-3333-333333333333"
        };

        var document = McpDiscoveryDocument.Create(options, "https://factlineage.example.test/");

        Assert.Equal("/.well-known/factlineage-mcp.json", McpDiscoveryDocument.DiscoveryPath);
        Assert.Equal("/.well-known/aidoc-mcp.json", McpDiscoveryDocument.LegacyDiscoveryPath);
        Assert.Equal("33333333-3333-3333-3333-333333333333", document.ClientId);
        Assert.Equal("api://33333333-3333-3333-3333-333333333333/access_as_user", document.DelegatedScope);
        Assert.Equal("https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/v2.0", document.Authority);
        Assert.Equal("https://factlineage.example.test/mcp", document.McpEndpoint);
        Assert.DoesNotContain(options.ManagedIdentityClientId, System.Text.Json.JsonSerializer.Serialize(document), StringComparison.Ordinal);
    }
}