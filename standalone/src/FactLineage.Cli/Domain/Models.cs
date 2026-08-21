namespace FactLineage.Cli.Domain;

public sealed record Project(string Id, string Name, string RepositoryPath, string? RemoteUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CreateProjectRequest(string Name, string RepositoryPath, string? RemoteUrl = null);

public sealed record UpdateProjectRequest(string? NewName, string? RepositoryPath, string? RemoteUrl, bool ClearRemoteUrl = false);

public sealed record ProjectRemovalResult(Project Project, int MemoriesRemoved, int MemoryVersionsRemoved, int SearchDocumentsRemoved);

public sealed record CodeReference(string Path, string? Symbol, int StartLine, int EndLine);

public sealed record MemoryReportRequest(
    string Type,
    string Title,
    string Summary,
    object? Details,
    IReadOnlyList<CodeReference> CodeReferences,
    string CreatedBy);

public sealed record MemoryRevisionRequest(
    string Summary,
    object? Details,
    IReadOnlyList<CodeReference> CodeReferences,
    string CreatedBy);

public sealed record Memory(string Id, string ProjectId, string Type, string Title, int CurrentVersion, DateTimeOffset CreatedAt);

public sealed record MemoryVersion(
    string Id,
    string MemoryId,
    int Version,
    string Summary,
    string DetailsJson,
    string CodeReferencesJson,
    string? CommitSha,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? EmbeddingModel);

public sealed record MemorySearchResult(Project Project, Memory Memory, MemoryVersion Version, double Score);

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}