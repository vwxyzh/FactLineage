using AiDoc.Cloud.Api.Configuration;

namespace AiDoc.Cloud.Api.Api;

public sealed record McpDiscoveryDocument(
    int SchemaVersion,
    string TenantId,
    string ClientId,
    string ApiAudience,
    string DelegatedScope,
    string Authority,
    string McpEndpoint,
    string Documentation,
    string Llms)
{
    public static McpDiscoveryDocument Create(CloudOptions cloud, string origin)
    {
        var clientId = cloud.ApiAudience["api://".Length..];
        var normalizedOrigin = origin.TrimEnd('/');
        return new McpDiscoveryDocument(
            1,
            cloud.TenantId,
            clientId,
            cloud.ApiAudience,
            $"{cloud.ApiAudience}/access_as_user",
            $"https://login.microsoftonline.com/{cloud.TenantId}/v2.0",
            $"{normalizedOrigin}/mcp",
            $"{normalizedOrigin}/docs/",
            $"{normalizedOrigin}/llms.txt");
    }
}