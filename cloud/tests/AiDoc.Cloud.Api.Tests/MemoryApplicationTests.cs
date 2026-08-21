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
            "tenant:actor",
            CancellationToken.None);

        Assert.Equal("pending", result.IndexingStatus);
        Assert.Equal(result.Memory, await repository.GetMemoryAsync(result.Memory.MemoryId, CancellationToken.None));
    }

    [Fact]
    public async Task Search_UsesKeywordQueryWhenEmbeddingFails()
    {
        var repository = new FakeRepository { NeedsReview = true };
        var memory = await repository.CreateMemoryAsync(
            repository.ProjectId,
            new ReportMemoryRequest("feature", "Login", "Issues a token.", null, [new CodeReference("Login.cs", "Login", 1, 2)], "test"),
            "Login Issues a token",
            "tenant:actor",
            CancellationToken.None);
        var search = new FakeSearchIndex { SearchHits = [new SearchHit(memory.MemoryId, 0.75)] };
        var application = new MemoryApplication(repository, search, new ThrowingEmbeddingService(), NullLogger<MemoryApplication>.Instance);

        var result = Assert.Single(await application.SearchAsync(repository.ProjectId, "authentication", null, 10, CancellationToken.None));

        Assert.Equal(memory.MemoryId, result.Memory.MemoryId);
        Assert.Null(search.LastSearchEmbedding);
        Assert.Equal(0.75, result.Score);
        Assert.True(result.FeedbackSummary.NeedsReview);
        Assert.Equal(1, result.FeedbackSummary.StaleCount);
    }

    [Fact]
    public async Task Report_PersistsTrustedActorAndPrefersAgentName()
    {
        var application = new MemoryApplication(new FakeRepository(), new FakeSearchIndex(), new FakeEmbeddingService(), NullLogger<MemoryApplication>.Instance);

        var result = await application.ReportAsync(
            Guid.NewGuid(),
            new ReportMemoryRequest("decision", "Identity", "Separates actor and agent.", null, [], "legacy-label", "GitHub Copilot"),
            "tenant:object-id",
            CancellationToken.None);

        Assert.Equal("tenant:object-id", result.Memory.ActorId);
        Assert.Equal("GitHub Copilot", result.Memory.AgentName);
        Assert.Equal("GitHub Copilot", result.Memory.CreatedBy);
    }

    [Fact]
    public async Task Report_AcceptsLegacyCreatedByAsAgentName()
    {
        var application = new MemoryApplication(new FakeRepository(), new FakeSearchIndex(), new FakeEmbeddingService(), NullLogger<MemoryApplication>.Instance);

        var result = await application.ReportAsync(
            Guid.NewGuid(),
            new ReportMemoryRequest("feature", "Legacy", "Legacy author label.", null, [], "legacy-agent"),
            "tenant:object-id",
            CancellationToken.None);

        Assert.Equal("legacy-agent", result.Memory.AgentName);
        Assert.Equal("tenant:object-id", result.Memory.ActorId);
    }

    [Fact]
    public async Task Revise_PersistsCurrentTrustedActorAndAgentName()
    {
        var repository = new FakeRepository();
        var application = new MemoryApplication(repository, new FakeSearchIndex(), new FakeEmbeddingService(), NullLogger<MemoryApplication>.Instance);
        var original = await application.ReportAsync(
            repository.ProjectId,
            new ReportMemoryRequest("feature", "Versioned", "Version one.", null, [], AgentName: "First Agent"),
            "tenant:first-actor",
            CancellationToken.None);

        var revised = await application.ReviseAsync(
            original.Memory.MemoryId,
            new ReviseMemoryRequest("Version two.", null, [], AgentName: "Second Agent"),
            "tenant:second-actor",
            CancellationToken.None);

        Assert.Equal(2, revised.Memory.Version);
        Assert.Equal("Second Agent", revised.Memory.AgentName);
        Assert.Equal("tenant:second-actor", revised.Memory.ActorId);
    }

    [Fact]
    public async Task SubmitFeedback_NormalizesAndForwardsAuthenticatedActor()
    {
        var repository = new FakeRepository();
        var application = new MemoryApplication(repository, new FakeSearchIndex(), new FakeEmbeddingService(), NullLogger<MemoryApplication>.Instance);

        var result = await application.SubmitFeedbackAsync(
            Guid.NewGuid(),
            2,
            "tenant:actor",
            new MemoryFeedbackRequest(" NOT_USEFUL ", " STALE ", "  verify source  ", "  original query  "),
            CancellationToken.None);

        Assert.Equal("tenant:actor", repository.LastActorId);
        Assert.Equal("not_useful", repository.LastFeedbackRequest!.Sentiment);
        Assert.Equal("stale", repository.LastFeedbackRequest.Reason);
        Assert.Equal("verify source", repository.LastFeedbackRequest.Comment);
        Assert.Equal("original query", repository.LastFeedbackRequest.SearchQuery);
        Assert.True(result.Summary.NeedsReview);
    }

    [Theory]
    [InlineData("useful", "stale")]
    [InlineData("not_useful", "useful")]
    [InlineData("not_useful", "unknown")]
    [InlineData("other", "useful")]
    public async Task SubmitFeedback_RejectsInvalidSentimentReasonPairs(string sentiment, string reason)
    {
        var application = new MemoryApplication(new FakeRepository(), new FakeSearchIndex(), new FakeEmbeddingService(), NullLogger<MemoryApplication>.Instance);

        await Assert.ThrowsAsync<DomainException>(() => application.SubmitFeedbackAsync(
            Guid.NewGuid(),
            1,
            "tenant:actor",
            new MemoryFeedbackRequest(sentiment, reason),
            CancellationToken.None));
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
        public bool NeedsReview { get; init; }
        public string? LastActorId { get; private set; }
        public MemoryFeedbackRequest? LastFeedbackRequest { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProjectRecord> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectRecord(ProjectId, request.Name, request.RepositoryUrl, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectRecord>>([new ProjectRecord(ProjectId, "first", null, DateTimeOffset.UtcNow)]);

        public Task<MemoryRecord> CreateMemoryAsync(Guid projectId, ReportMemoryRequest request, string contentText, string actorId, CancellationToken cancellationToken)
        {
            var memory = new MemoryRecord(Guid.NewGuid(), projectId, request.Type, request.Title, 1, request.Summary, request.Details, request.CodeReferences, contentText, request.AgentName!, DateTimeOffset.UtcNow, actorId);
            _memories[memory.MemoryId] = memory;
            return Task.FromResult(memory);
        }

        public Task<MemoryRecord> ReviseMemoryAsync(Guid memoryId, ReviseMemoryRequest request, string contentText, string actorId, CancellationToken cancellationToken)
        {
            var current = _memories[memoryId];
            var revised = current with
            {
                Version = current.Version + 1,
                Summary = request.Summary,
                Details = request.Details,
                CodeReferences = request.CodeReferences,
                ContentText = contentText,
                CreatedBy = request.AgentName!,
                CreatedAt = DateTimeOffset.UtcNow,
                ActorId = actorId
            };
            _memories[memoryId] = revised;
            return Task.FromResult(revised);
        }

        public Task<MemoryRecord?> GetMemoryAsync(Guid memoryId, CancellationToken cancellationToken) =>
            Task.FromResult(_memories.GetValueOrDefault(memoryId));

        public Task<IReadOnlyList<MemoryRecord>> GetMemoriesAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryRecord>>(memoryIds.Where(_memories.ContainsKey).Select(id => _memories[id]).ToList());

        public Task<IReadOnlyList<MemoryRecord>> ListCurrentMemoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryRecord>>(_memories.Values.ToList());

        public Task<MemoryFeedbackResult> UpsertFeedbackAsync(Guid memoryId, int version, string actorId, MemoryFeedbackRequest request, CancellationToken cancellationToken)
        {
            LastActorId = actorId;
            LastFeedbackRequest = request;
            var needsReview = request.Reason is "incorrect" or "stale" or "missing_evidence";
            var summary = Summary(memoryId, version, needsReview);
            return Task.FromResult(new MemoryFeedbackResult(memoryId, version, request.Sentiment, request.Reason, request.Comment, DateTimeOffset.UtcNow, summary));
        }

        public Task<MemoryFeedbackSummary> DeleteFeedbackAsync(Guid memoryId, int version, string actorId, CancellationToken cancellationToken) =>
            Task.FromResult(Summary(memoryId, version, false));

        public Task<MemoryFeedbackSummary> GetFeedbackSummaryAsync(Guid memoryId, int version, CancellationToken cancellationToken) =>
            Task.FromResult(Summary(memoryId, version, NeedsReview));

        public Task<IReadOnlyDictionary<Guid, MemoryFeedbackSummary>> GetCurrentFeedbackSummariesAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, MemoryFeedbackSummary>>(memoryIds.ToDictionary(memoryId => memoryId, memoryId => Summary(memoryId, _memories[memoryId].Version, NeedsReview)));

        private static MemoryFeedbackSummary Summary(Guid memoryId, int version, bool needsReview) =>
            new(memoryId, version, 0, needsReview ? 1 : 0, 0, needsReview ? 1 : 0, 0, 0, needsReview);
    }
}