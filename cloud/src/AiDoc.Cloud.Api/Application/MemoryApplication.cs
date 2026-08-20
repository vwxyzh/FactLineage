using System.Text.Json;
using AiDoc.Cloud.Api.Domain;

namespace AiDoc.Cloud.Api.Application;

public sealed class MemoryApplication(
    IMemoryRepository repository,
    IMemorySearchIndex searchIndex,
    IEmbeddingService embeddings,
    ILogger<MemoryApplication> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<ProjectRecord> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new DomainException("INVALID_PROJECT_NAME", "Project name is required.");
        }

        return repository.CreateProjectAsync(request with { Name = request.Name.Trim() }, cancellationToken);
    }

    public Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken) =>
        repository.ListProjectsAsync(cancellationToken);

    public async Task<MemoryWriteResult> ReportAsync(Guid projectId, ReportMemoryRequest request, CancellationToken cancellationToken)
    {
        Validate(request.Type, request.Title, request.Summary, request.CodeReferences, request.CreatedBy);
        var normalized = request with
        {
            Type = request.Type.Trim().ToLowerInvariant(),
            Title = request.Title.Trim(),
            Summary = request.Summary.Trim(),
            CreatedBy = request.CreatedBy.Trim()
        };
        var contentText = CreateContentText(normalized.Title, normalized.Summary, normalized.Details, normalized.CodeReferences);
        var embedding = await TryCreateEmbeddingAsync(contentText, cancellationToken);
        var memory = await repository.CreateMemoryAsync(projectId, normalized, contentText, cancellationToken);
        return new MemoryWriteResult(memory, await TryIndexAsync(memory, embedding, cancellationToken));
    }

    public async Task<MemoryWriteResult> ReviseAsync(Guid memoryId, ReviseMemoryRequest request, CancellationToken cancellationToken)
    {
        Validate("feature", "revision", request.Summary, request.CodeReferences, request.CreatedBy);
        var current = await repository.GetMemoryAsync(memoryId, cancellationToken)
            ?? throw new DomainException("MEMORY_NOT_FOUND", $"Memory '{memoryId}' was not found.");
        var normalized = request with { Summary = request.Summary.Trim(), CreatedBy = request.CreatedBy.Trim() };
        var contentText = CreateContentText(current.Title, normalized.Summary, normalized.Details, normalized.CodeReferences);
        var embedding = await TryCreateEmbeddingAsync(contentText, cancellationToken);
        var memory = await repository.ReviseMemoryAsync(memoryId, normalized, contentText, cancellationToken);
        return new MemoryWriteResult(memory, await TryIndexAsync(memory, embedding, cancellationToken));
    }

    public async Task<MemoryRecord> GetAsync(Guid memoryId, CancellationToken cancellationToken) =>
        await repository.GetMemoryAsync(memoryId, cancellationToken)
        ?? throw new DomainException("MEMORY_NOT_FOUND", $"Memory '{memoryId}' was not found.");

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(Guid projectId, string query, string? type, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new DomainException("INVALID_SEARCH_QUERY", "Search query is required.");
        }

        if (limit is < 1 or > 100)
        {
            throw new DomainException("INVALID_LIMIT", "Search limit must be between 1 and 100.");
        }

        var embedding = await TryCreateEmbeddingAsync(query.Trim(), cancellationToken);
        var hits = await searchIndex.SearchAsync(projectId, query.Trim(), type, limit, embedding, cancellationToken);
        var memories = await repository.GetMemoriesAsync(hits.Select(hit => hit.MemoryId).ToList(), cancellationToken);
        var memoryById = memories.ToDictionary(memory => memory.MemoryId);
        return hits
            .Where(hit => memoryById.ContainsKey(hit.MemoryId))
            .Select(hit => new MemorySearchResult(memoryById[hit.MemoryId], hit.Score))
            .ToList();
    }

    public async Task<ReindexResult> ReindexAsync(CancellationToken cancellationToken)
    {
        var memories = await repository.ListCurrentMemoriesAsync(cancellationToken);
        var indexed = 0;
        foreach (var memory in memories)
        {
            var embedding = await TryCreateEmbeddingAsync(memory.ContentText, cancellationToken);
            if (await TryIndexAsync(memory, embedding, cancellationToken) == "complete")
            {
                indexed++;
            }
        }

        return new ReindexResult(memories.Count, indexed, memories.Count - indexed);
    }

    private async Task<ReadOnlyMemory<float>?> TryCreateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            return await embeddings.CreateAsync(text, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Embedding generation failed; continuing without a vector.");
            return null;
        }
    }

    private async Task<string> TryIndexAsync(MemoryRecord memory, ReadOnlyMemory<float>? embedding, CancellationToken cancellationToken)
    {
        try
        {
            await searchIndex.UpsertAsync(memory, embedding, cancellationToken);
            return "complete";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Search indexing failed for memory {MemoryId} version {Version}.", memory.MemoryId, memory.Version);
            return "pending";
        }
    }

    private static string CreateContentText(string title, string summary, JsonElement? details, IReadOnlyList<CodeReference> references)
    {
        var symbols = string.Join(' ', references.Select(reference => reference.Symbol).Where(symbol => !string.IsNullOrWhiteSpace(symbol)));
        return $"{title}\n{summary}\n{JsonSerializer.Serialize(details, JsonOptions)}\n{symbols}";
    }

    private static void Validate(string type, string title, string summary, IReadOnlyList<CodeReference> references, string createdBy)
    {
        if (type.Trim().ToLowerInvariant() is not ("feature" or "api" or "decision"))
        {
            throw new DomainException("INVALID_MEMORY_TYPE", "Memory type must be feature, api, or decision.");
        }

        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("INVALID_MEMORY_TITLE", "Memory title is required.");
        if (string.IsNullOrWhiteSpace(summary)) throw new DomainException("INVALID_MEMORY_SUMMARY", "Memory summary is required.");
        if (string.IsNullOrWhiteSpace(createdBy)) throw new DomainException("INVALID_CREATED_BY", "CreatedBy is required.");
        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.Path) || reference.StartLine < 1 || reference.EndLine < reference.StartLine)
            {
                throw new DomainException("INVALID_CODE_REFERENCE", "Code references must have a path and valid line range.");
            }
        }
    }
}