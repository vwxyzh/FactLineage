using System.ComponentModel;
using AiDoc.Cloud.Api.Application;
using AiDoc.Cloud.Api.Domain;
using ModelContextProtocol.Server;

namespace AiDoc.Cloud.Api.Api;

[McpServerToolType]
public sealed class MemoryTools(MemoryApplication application)
{
    [McpServerTool(Name = "create_project")]
    [Description("Creates an AI Doc project used to scope memories and searches.")]
    public Task<ProjectRecord> CreateProjectAsync(
        [Description("Unique project name.")] string name,
        [Description("Optional source repository URL.")] string? repositoryUrl = null,
        CancellationToken cancellationToken = default) =>
        application.CreateProjectAsync(new CreateProjectRequest(name, repositoryUrl), cancellationToken);

    [McpServerTool(Name = "list_projects")]
    [Description("Lists AI Doc projects and their identifiers.")]
    public Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        application.ListProjectsAsync(cancellationToken);

    [McpServerTool(Name = "report_memory")]
    [Description("Creates a project memory and indexes its current version for semantic search.")]
    public Task<MemoryWriteResult> ReportMemoryAsync(
        [Description("Project identifier.")] Guid projectId,
        [Description("Memory report including type, title, summary, details, code references, and author.")] ReportMemoryRequest request,
        CancellationToken cancellationToken) =>
        application.ReportAsync(projectId, request, cancellationToken);

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
}