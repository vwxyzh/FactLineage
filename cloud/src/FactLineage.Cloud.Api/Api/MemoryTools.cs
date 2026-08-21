using System.ComponentModel;
using FactLineage.Cloud.Api.Application;
using FactLineage.Cloud.Api.Domain;
using ModelContextProtocol.Server;

namespace FactLineage.Cloud.Api.Api;

[McpServerToolType]
public sealed class MemoryTools(MemoryApplication application, IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "create_project")]
    [Description("Creates a FactLineage project used to scope memories and searches.")]
    public Task<ProjectRecord> CreateProjectAsync(
        [Description("Unique project name.")] string name,
        [Description("Optional source repository URL.")] string? repositoryUrl = null,
        CancellationToken cancellationToken = default) =>
        application.CreateProjectAsync(new CreateProjectRequest(name, repositoryUrl), cancellationToken);

    [McpServerTool(Name = "list_projects")]
    [Description("Lists FactLineage projects and their identifiers.")]
    public Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        application.ListProjectsAsync(cancellationToken);

    [McpServerTool(Name = "report_memory")]
    [Description("Creates a project memory and indexes its current version for semantic search.")]
    public Task<MemoryWriteResult> ReportMemoryAsync(
        [Description("Project identifier.")] Guid projectId,
        [Description("Memory report including type, title, summary, details, code references, and agentName. Legacy createdBy is accepted as the agent display label.")] ReportMemoryRequest request,
        CancellationToken cancellationToken) =>
        application.ReportAsync(
            projectId,
            request,
            ActorIdentity.FromClaims(httpContextAccessor.HttpContext?.User),
            cancellationToken);

    [McpServerTool(Name = "search_memories")]
    [Description("Searches current memory versions within one project using keyword, vector, and semantic retrieval.")]
    public Task<IReadOnlyList<MemorySearchResult>> SearchMemoriesAsync(
        [Description("Project identifier.")] Guid projectId,
        [Description("Natural-language search query.")] string query,
        [Description("Optional feature, api, or decision filter.")] string? type = null,
        [Description("Maximum number of results from 1 through 100.")] int limit = 10,
        CancellationToken cancellationToken = default) =>
        application.SearchAsync(projectId, query, type, limit, cancellationToken);

    [McpServerTool(Name = "get_memory")]
    [Description("Gets the current immutable version of a memory and its code references.")]
    public Task<MemoryRecord> GetMemoryAsync(
        [Description("Memory identifier.")] Guid memoryId,
        CancellationToken cancellationToken) =>
        application.GetAsync(memoryId, cancellationToken);

    [McpServerTool(Name = "submit_memory_feedback")]
    [Description("Submits or replaces the authenticated caller's quality feedback for one immutable memory version.")]
    public Task<MemoryFeedbackResult> SubmitMemoryFeedbackAsync(
        [Description("Memory identifier.")] Guid memoryId,
        [Description("Immutable memory version number.")] int version,
        [Description("useful or not_useful.")] string sentiment,
        [Description("useful, incorrect, stale, irrelevant, or missing_evidence.")] string reason,
        [Description("Optional concise correction or context, up to 2000 characters.")] string? comment = null,
        [Description("Optional search query that produced the result, up to 2000 characters.")] string? searchQuery = null,
        CancellationToken cancellationToken = default) =>
        application.SubmitFeedbackAsync(
            memoryId,
            version,
            ActorIdentity.FromClaims(httpContextAccessor.HttpContext?.User),
            new MemoryFeedbackRequest(sentiment, reason, comment, searchQuery),
            cancellationToken);

    [McpServerTool(Name = "get_memory_feedback_summary")]
    [Description("Gets aggregate quality signals and review state for one immutable memory version.")]
    public Task<MemoryFeedbackSummary> GetMemoryFeedbackSummaryAsync(
        [Description("Memory identifier.")] Guid memoryId,
        [Description("Immutable memory version number.")] int version,
        CancellationToken cancellationToken = default) =>
        application.GetFeedbackSummaryAsync(memoryId, version, cancellationToken);
}