using AiDoc.Cloud.Api.Application;
using AiDoc.Cloud.Api.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiDoc.Cloud.Api.Tests;

public sealed class MemoryApplicationTests
{
    [Fact]
    public async Task Report_PersistsMemoryWhenSearchIndexingFails()
    {
        var repository = new FakeRepository();
        var search = new FakeSearchIndex { ThrowOnUpsert = true };
        var application = new MemoryApplication(repository, search, new FakeEmbeddingService(), NullLogger<MemoryApplication>.Instance);

        var result = await application.ReportAsync(
            repository.ProjectId,
            new ReportMemoryRequest("feature", "Login", "Issues a token.", null, [new CodeReference("Login.cs", "Login", 1, 2)], "test"),
            CancellationToken.None);

        Assert.Equal("pending", result.IndexingStatus);
        Assert.Equal(result.Memory, await repository.GetMemoryAsync(result.Memory.MemoryId, CancellationToken.None));
    }

    [Fact]
    public async Task Search_UsesKeywordQueryWhenEmbeddingFails()
    {
        var repository = new FakeRepository();
        var memory = await repository.CreateMemoryAsync(
            repository.ProjectId,
            new ReportMemoryRequest("feature", "Login", "Issues a token.", null, [new CodeReference("Login.cs", "Login", 1, 2)], "test"),
            "Login Issues a token",
            CancellationToken.None);
        var search = new FakeSearchIndex { SearchHits = [new SearchHit(memory.MemoryId, 0.75)] };
        var application = new MemoryApplication(repository, search, new ThrowingEmbeddingService(), NullLogger<MemoryApplication>.Instance);

        var result = Assert.Single(await application.SearchAsync(repository.ProjectId, "authentication", null, 10, CancellationToken.None));

        Assert.Equal(memory.MemoryId, result.Memory.MemoryId);
        Assert.Null(search.LastSearchEmbedding);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> CreateAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1, 0 });
    }

    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> CreateAsync(string text, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Embedding service unavailable.");
    }

    private sealed class FakeSearchIndex : IMemorySearchIndex
    {
        public bool ThrowOnUpsert { get; init; }
        public IReadOnlyList<SearchHit> SearchHits { get; init; } = [];
        public ReadOnlyMemory<float>? LastSearchEmbedding { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertAsync(MemoryRecord memory, ReadOnlyMemory<float>? embedding, CancellationToken cancellationToken) =>
            ThrowOnUpsert ? throw new InvalidOperationException("Index unavailable.") : Task.CompletedTask;

        public Task<IReadOnlyList<SearchHit>> SearchAsync(Guid projectId, string query, string? type, int limit, ReadOnlyMemory<float>? embedding, CancellationToken cancellationToken)
        {
            LastSearchEmbedding = embedding;
            return Task.FromResult(SearchHits);
        }
    }

    private sealed class FakeRepository : IMemoryRepository
    {
        private readonly Dictionary<Guid, MemoryRecord> _memories = [];
        public Guid ProjectId { get; } = Guid.NewGuid();

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProjectRecord> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectRecord(ProjectId, request.Name, request.RepositoryUrl, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectRecord>>([new ProjectRecord(ProjectId, "first", null, DateTimeOffset.UtcNow)]);

        public Task<MemoryRecord> CreateMemoryAsync(Guid projectId, ReportMemoryRequest request, string contentText, CancellationToken cancellationToken)
        {
            var memory = new MemoryRecord(Guid.NewGuid(), projectId, request.Type, request.Title, 1, request.Summary, request.Details, request.CodeReferences, contentText, request.CreatedBy, DateTimeOffset.UtcNow);
            _memories[memory.MemoryId] = memory;
            return Task.FromResult(memory);
        }

        public Task<MemoryRecord> ReviseMemoryAsync(Guid memoryId, ReviseMemoryRequest request, string contentText, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MemoryRecord?> GetMemoryAsync(Guid memoryId, CancellationToken cancellationToken) =>
            Task.FromResult(_memories.GetValueOrDefault(memoryId));

        public Task<IReadOnlyList<MemoryRecord>> GetMemoriesAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryRecord>>(memoryIds.Where(_memories.ContainsKey).Select(id => _memories[id]).ToList());

        public Task<IReadOnlyList<MemoryRecord>> ListCurrentMemoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryRecord>>(_memories.Values.ToList());
    }
}