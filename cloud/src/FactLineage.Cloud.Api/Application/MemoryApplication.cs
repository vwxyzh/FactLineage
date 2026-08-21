using System.Text.Json;
using FactLineage.Cloud.Api.Domain;

namespace FactLineage.Cloud.Api.Application;

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

    public async Task<MemoryWriteResult> ReportAsync(Guid projectId, ReportMemoryRequest request, string actorId, CancellationToken cancellationToken)
    {
        var agentName = ResolveAgentName(request.AgentName, request.CreatedBy);
        Validate(request.Type, request.Title, request.Summary, request.CodeReferences, agentName, actorId);
        var normalized = request with
        {
            Type = request.Type.Trim().ToLowerInvariant(),
            Title = request.Title.Trim(),
            Summary = request.Summary.Trim(),
            CreatedBy = agentName,
            AgentName = agentName
        };
        var contentText = CreateContentText(normalized.Title, normalized.Summary, normalized.Details, normalized.CodeReferences);
        var embedding = await TryCreateEmbeddingAsync(contentText, cancellationToken);
        var memory = await repository.CreateMemoryAsync(projectId, normalized, contentText, actorId, cancellationToken);
        return new MemoryWriteResult(memory, await TryIndexAsync(memory, embedding, cancellationToken));
    }

    public async Task<MemoryWriteResult> ReviseAsync(Guid memoryId, ReviseMemoryRequest request, string actorId, CancellationToken cancellationToken)
    {
        var agentName = ResolveAgentName(request.AgentName, request.CreatedBy);
        Validate("feature", "revision", request.Summary, request.CodeReferences, agentName, actorId);
        var current = await repository.GetMemoryAsync(memoryId, cancellationToken)
            ?? throw new DomainException("MEMORY_NOT_FOUND", $"Memory '{memoryId}' was not found.");
        var normalized = request with { Summary = request.Summary.Trim(), CreatedBy = agentName, AgentName = agentName };
        var contentText = CreateContentText(current.Title, normalized.Summary, normalized.Details, normalized.CodeReferences);
        var embedding = await TryCreateEmbeddingAsync(contentText, cancellationToken);
        var memory = await repository.ReviseMemoryAsync(memoryId, normalized, contentText, actorId, cancellationToken);
        return new MemoryWriteResult(memory, await TryIndexAsync(memory, embedding, cancellationToken));
    }

    public async Task<MemoryRecord> GetAsync(Guid memoryId, CancellationToken cancellationToken)
    {
        var memory = await repository.GetMemoryAsync(memoryId, cancellationToken)
            ?? throw new DomainException("MEMORY_NOT_FOUND", $"Memory '{memoryId}' was not found.");
        var feedbackSummary = await repository.GetFeedbackSummaryAsync(memory.MemoryId, memory.Version, cancellationToken);
        return memory with { FeedbackSummary = feedbackSummary };
    }

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
        var feedbackByMemoryId = await repository.GetCurrentFeedbackSummariesAsync(memoryById.Keys.ToList(), cancellationToken);
        return hits
            .Where(hit => memoryById.ContainsKey(hit.MemoryId))
            .Select(hit => new MemorySearchResult(memoryById[hit.MemoryId], hit.Score, feedbackByMemoryId[hit.MemoryId]))
            .ToList();
    }

    public Task<MemoryFeedbackResult> SubmitFeedbackAsync(Guid memoryId, int version, string actorId, MemoryFeedbackRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new DomainException("ACTOR_IDENTITY_REQUIRED", "A stable authenticated actor identity is required.");
        }

        if (version < 1)
        {
            throw new DomainException("INVALID_MEMORY_VERSION", "Memory version must be greater than zero.");
        }

        var sentiment = request.Sentiment?.Trim().ToLowerInvariant() ?? string.Empty;
        var reason = request.Reason?.Trim().ToLowerInvariant() ?? string.Empty;
        if (sentiment is not ("useful" or "not_useful"))
        {
            throw new DomainException("INVALID_FEEDBACK_SENTIMENT", "Feedback sentiment must be useful or not_useful.");
        }

        var validReason = sentiment == "useful"
            ? reason == "useful"
            : reason is "incorrect" or "stale" or "irrelevant" or "missing_evidence";
        if (!validReason)
        {
            throw new DomainException("INVALID_FEEDBACK_REASON", "Useful feedback requires reason useful; not_useful feedback requires incorrect, stale, irrelevant, or missing_evidence.");
        }

        var comment = NormalizeOptionalText(request.Comment, 2000, "INVALID_FEEDBACK_COMMENT", "Feedback comment must not exceed 2000 characters.");
        var searchQuery = NormalizeOptionalText(request.SearchQuery, 2000, "INVALID_FEEDBACK_QUERY", "Feedback search query must not exceed 2000 characters.");
        return repository.UpsertFeedbackAsync(memoryId, version, actorId, request with
        {
            Sentiment = sentiment,
            Reason = reason,
            Comment = comment,
            SearchQuery = searchQuery
        }, cancellationToken);
    }

    public Task<MemoryFeedbackSummary> DeleteFeedbackAsync(Guid memoryId, int version, string actorId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new DomainException("ACTOR_IDENTITY_REQUIRED", "A stable authenticated actor identity is required.");
        }

        return repository.DeleteFeedbackAsync(memoryId, version, actorId, cancellationToken);
    }

    public Task<MemoryFeedbackSummary> GetFeedbackSummaryAsync(Guid memoryId, int version, CancellationToken cancellationToken) =>
        repository.GetFeedbackSummaryAsync(memoryId, version, cancellationToken);

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

    private static string? NormalizeOptionalText(string? value, int maximumLength, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maximumLength) throw new DomainException(code, message);
        return normalized;
    }

    private static string ResolveAgentName(string? agentName, string? createdBy)
    {
        var value = !string.IsNullOrWhiteSpace(agentName) ? agentName : createdBy;
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException("INVALID_AGENT_NAME", "AgentName is required; createdBy is accepted for backward compatibility.");
        var normalized = value.Trim();
        if (normalized.Length > 200) throw new DomainException("INVALID_AGENT_NAME", "AgentName must not exceed 200 characters.");
        return normalized;
    }

    private static void Validate(string type, string title, string summary, IReadOnlyList<CodeReference> references, string agentName, string actorId)
    {
        if (type.Trim().ToLowerInvariant() is not ("feature" or "api" or "decision"))
        {
            throw new DomainException("INVALID_MEMORY_TYPE", "Memory type must be feature, api, or decision.");
        }

        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("INVALID_MEMORY_TITLE", "Memory title is required.");
        if (string.IsNullOrWhiteSpace(summary)) throw new DomainException("INVALID_MEMORY_SUMMARY", "Memory summary is required.");
        if (string.IsNullOrWhiteSpace(agentName)) throw new DomainException("INVALID_AGENT_NAME", "AgentName is required.");
        if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("ACTOR_IDENTITY_REQUIRED", "A stable authenticated actor identity is required.");
        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.Path) || reference.StartLine < 1 || reference.EndLine < reference.StartLine)
            {
                throw new DomainException("INVALID_CODE_REFERENCE", "Code references must have a path and valid line range.");
            }
        }
    }
}