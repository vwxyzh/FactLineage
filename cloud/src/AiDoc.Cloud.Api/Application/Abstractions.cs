using AiDoc.Cloud.Api.Domain;

namespace AiDoc.Cloud.Api.Application;

public interface IEmbeddingService
{
    Task<ReadOnlyMemory<float>> CreateAsync(string text, CancellationToken cancellationToken);
}

public interface IMemoryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ProjectRecord> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken);
    Task<MemoryRecord> CreateMemoryAsync(Guid projectId, ReportMemoryRequest request, string contentText, string actorId, CancellationToken cancellationToken);
    Task<MemoryRecord> ReviseMemoryAsync(Guid memoryId, ReviseMemoryRequest request, string contentText, string actorId, CancellationToken cancellationToken);
    Task<MemoryRecord?> GetMemoryAsync(Guid memoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> GetMemoriesAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListCurrentMemoriesAsync(CancellationToken cancellationToken);
    Task<MemoryFeedbackResult> UpsertFeedbackAsync(Guid memoryId, int version, string actorId, MemoryFeedbackRequest request, CancellationToken cancellationToken);
    Task<MemoryFeedbackSummary> DeleteFeedbackAsync(Guid memoryId, int version, string actorId, CancellationToken cancellationToken);
    Task<MemoryFeedbackSummary> GetFeedbackSummaryAsync(Guid memoryId, int version, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, MemoryFeedbackSummary>> GetCurrentFeedbackSummariesAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken);
}

public interface IMemorySearchIndex
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task UpsertAsync(MemoryRecord memory, ReadOnlyMemory<float>? embedding, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchHit>> SearchAsync(Guid projectId, string query, string? type, int limit, ReadOnlyMemory<float>? embedding, CancellationToken cancellationToken);
}

public sealed record SearchHit(Guid MemoryId, double Score);