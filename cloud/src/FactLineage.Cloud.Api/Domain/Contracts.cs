using System.Text.Json;

namespace FactLineage.Cloud.Api.Domain;

public sealed record CodeReference(string Path, string? Symbol, int StartLine, int EndLine);

public sealed record CreateProjectRequest(string Name, string? RepositoryUrl);

public sealed record ProjectRecord(
    Guid Id,
    string Name,
    string? RepositoryUrl,
    DateTimeOffset CreatedAt);

public sealed record ReportMemoryRequest(
    string Type,
    string Title,
    string Summary,
    JsonElement? Details,
    IReadOnlyList<CodeReference> CodeReferences,
    string? CreatedBy = null,
    string? AgentName = null);

public sealed record ReviseMemoryRequest(
    string Summary,
    JsonElement? Details,
    IReadOnlyList<CodeReference> CodeReferences,
    string? CreatedBy = null,
    string? AgentName = null);

public sealed record MemoryRecord(
    Guid MemoryId,
    Guid ProjectId,
    string Type,
    string Title,
    int Version,
    string Summary,
    JsonElement? Details,
    IReadOnlyList<CodeReference> CodeReferences,
    string ContentText,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ActorId = null,
    MemoryFeedbackSummary? FeedbackSummary = null)
{
    public string AgentName => CreatedBy;
}

public sealed record MemoryWriteResult(MemoryRecord Memory, string IndexingStatus);

public sealed record MemoryFeedbackRequest(
    string Sentiment,
    string Reason,
    string? Comment = null,
    string? SearchQuery = null);

public sealed record MemoryFeedbackSummary(
    Guid MemoryId,
    int Version,
    int UsefulCount,
    int NotUsefulCount,
    int IncorrectCount,
    int StaleCount,
    int IrrelevantCount,
    int MissingEvidenceCount,
    bool NeedsReview);

public sealed record MemoryFeedbackResult(
    Guid MemoryId,
    int Version,
    string Sentiment,
    string Reason,
    string? Comment,
    DateTimeOffset UpdatedAt,
    MemoryFeedbackSummary Summary);

public sealed record MemorySearchResult(MemoryRecord Memory, double Score, MemoryFeedbackSummary FeedbackSummary);

public sealed record ReindexResult(int Scanned, int Indexed, int Pending);

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}