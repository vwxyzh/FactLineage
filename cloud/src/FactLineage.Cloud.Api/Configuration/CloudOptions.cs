using System.ComponentModel.DataAnnotations;

namespace FactLineage.Cloud.Api.Configuration;

public sealed class CloudOptions
{
    public const string SectionName = "Cloud";

    [Required]
    public string ManagedIdentityClientId { get; init; } = string.Empty;

    [Required]
    public string TenantId { get; init; } = string.Empty;

    [Required]
    public string ApiAudience { get; init; } = string.Empty;

    [Required]
    public PostgreSqlOptions PostgreSql { get; init; } = new();

    [Required]
    public SearchOptions Search { get; init; } = new();

    [Required]
    public OpenAiOptions OpenAi { get; init; } = new();
}

public sealed class PostgreSqlOptions
{
    [Required]
    public string Host { get; init; } = string.Empty;

    [Required]
    public string Database { get; init; } = string.Empty;

    [Required]
    public string User { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 5432;
}

public sealed class SearchOptions
{
    [Required, Url]
    public string Endpoint { get; init; } = string.Empty;

    [Required]
    public string IndexName { get; init; } = string.Empty;

    [Required]
    public string SemanticConfigurationName { get; init; } = string.Empty;
}

public sealed class OpenAiOptions
{
    [Required, Url]
    public string Endpoint { get; init; } = string.Empty;

    [Required]
    public string EmbeddingDeployment { get; init; } = string.Empty;

    [Range(1, 4096)]
    public int EmbeddingDimensions { get; init; } = 1536;
}