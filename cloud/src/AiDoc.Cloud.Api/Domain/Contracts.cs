using System.Text.Json;

namespace AiDoc.Cloud.Api.Domain;

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
    string CreatedBy);

public sealed record ReviseMemoryRequest(
    string Summary,
    JsonElement? Details,
    IReadOnlyList<CodeReference> CodeReferences,
    string CreatedBy);

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
    DateTimeOffset CreatedAt);

public sealed record MemoryWriteResult(MemoryRecord Memory, string IndexingStatus);

public sealed record MemorySearchResult(MemoryRecord Memory, double Score);

public sealed record ReindexResult(int Scanned, int Indexed, int Pending);

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}