using FactLineage.Cloud.Api.Application;
using FactLineage.Cloud.Api.Configuration;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;

namespace FactLineage.Cloud.Api.Infrastructure;

public sealed class AzureOpenAiEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;
    private readonly int _dimensions;

    public AzureOpenAiEmbeddingService(TokenCredential credential, IOptions<CloudOptions> options)
    {
        var openAi = options.Value.OpenAi;
        _client = new AzureOpenAIClient(new Uri(openAi.Endpoint), credential).GetEmbeddingClient(openAi.EmbeddingDeployment);
        _dimensions = openAi.EmbeddingDimensions;
    }

    public async Task<ReadOnlyMemory<float>> CreateAsync(string text, CancellationToken cancellationToken)
    {
        var options = new EmbeddingGenerationOptions { Dimensions = _dimensions };
        var response = await _client.GenerateEmbeddingAsync(text, options, cancellationToken);
        return response.Value.ToFloats();
    }
}