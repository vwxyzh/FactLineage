using AiDoc.Cloud.Api.Application;
using AiDoc.Cloud.Api.Configuration;
using AiDoc.Cloud.Api.Domain;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using CloudSearchOptions = AiDoc.Cloud.Api.Configuration.SearchOptions;

namespace AiDoc.Cloud.Api.Infrastructure;

public sealed class AzureSearchMemoryIndex : IMemorySearchIndex
{
    private const string VectorProfileName = "memory-vector-profile";
    private const string VectorAlgorithmName = "memory-hnsw";
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly CloudSearchOptions _options;
    private readonly int _embeddingDimensions;

    public AzureSearchMemoryIndex(TokenCredential credential, IOptions<CloudOptions> options)
    {
        _options = options.Value.Search;
        _embeddingDimensions = options.Value.OpenAi.EmbeddingDimensions;
        _indexClient = new SearchIndexClient(new Uri(_options.Endpoint), credential);
        _searchClient = _indexClient.GetSearchClient(_options.IndexName);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var index = new SearchIndex(_options.IndexName)
        {
            Fields =
            {
                new SimpleField("memoryId", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SimpleField("projectId", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("type", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("version", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
                new SearchableField("title") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchableField("summary") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchableField("contentText"),
                new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = _embeddingDimensions,
                    VectorSearchProfileName = VectorProfileName
                }
            },
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration(VectorAlgorithmName) },
                Profiles = { new VectorSearchProfile(VectorProfileName, VectorAlgorithmName) }
            },
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(_options.SemanticConfigurationName, new SemanticPrioritizedFields
                    {
                        TitleField = new SemanticField("title"),
                        ContentFields = { new SemanticField("summary"), new SemanticField("contentText") }
                    })
                }
            }
        };
        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
    }

    public async Task UpsertAsync(MemoryRecord memory, ReadOnlyMemory<float>? embedding, CancellationToken cancellationToken)
    {
        var document = new SearchDocument
        {
            ["memoryId"] = memory.MemoryId.ToString(),
            ["projectId"] = memory.ProjectId.ToString(),
            ["type"] = memory.Type,
            ["version"] = memory.Version,
            ["title"] = memory.Title,
            ["summary"] = memory.Summary,
            ["contentText"] = memory.ContentText
        };
        if (embedding.HasValue) document["embedding"] = embedding.Value.ToArray();
        await _searchClient.MergeOrUploadDocumentsAsync([document], cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(Guid projectId, string query, string? type, int limit, ReadOnlyMemory<float>? embedding, CancellationToken cancellationToken)
    {
        try
        {
            return await SearchCoreAsync(projectId, query, type, limit, embedding, useSemanticRanking: true, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 400 or 403)
        {
            return await SearchCoreAsync(projectId, query, type, limit, embedding, useSemanticRanking: false, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<SearchHit>> SearchCoreAsync(Guid projectId, string query, string? type, int limit, ReadOnlyMemory<float>? embedding, bool useSemanticRanking, CancellationToken cancellationToken)
    {
        var filter = $"projectId eq '{projectId}'";
        if (!string.IsNullOrWhiteSpace(type)) filter += $" and type eq '{EscapeFilter(type.Trim().ToLowerInvariant())}'";
        var options = new Azure.Search.Documents.SearchOptions
        {
            Filter = filter,
            Size = limit,
            QueryType = useSemanticRanking ? SearchQueryType.Semantic : SearchQueryType.Simple
        };
        options.Select.Add("memoryId");
        if (useSemanticRanking)
        {
            options.SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = _options.SemanticConfigurationName };
        }

        if (embedding.HasValue)
        {
            options.VectorSearch = new VectorSearchOptions();
            options.VectorSearch.Queries.Add(new VectorizedQuery(embedding.Value)
            {
                KNearestNeighborsCount = 50,
                Fields = { "embedding" }
            });
        }

        var response = await _searchClient.SearchAsync<SearchDocument>(query, options, cancellationToken);
        var results = new List<SearchHit>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            if (Guid.TryParse(result.Document["memoryId"]?.ToString(), out var memoryId))
            {
                results.Add(new SearchHit(memoryId, result.SemanticSearch?.RerankerScore ?? result.Score ?? 0));
            }
        }

        return results;
    }

    private static string EscapeFilter(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}