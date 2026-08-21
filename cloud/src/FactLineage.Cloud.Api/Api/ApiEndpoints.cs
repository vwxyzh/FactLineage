using FactLineage.Cloud.Api.Application;
using FactLineage.Cloud.Api.Domain;
using System.Security.Claims;

namespace FactLineage.Cloud.Api.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapFactLineageApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/v1").RequireAuthorization();
        api.MapPost("/projects", async (CreateProjectRequest request, MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Created($"/v1/projects/{request.Name}", await application.CreateProjectAsync(request, cancellationToken)));
        api.MapGet("/projects", async (MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Ok(await application.ListProjectsAsync(cancellationToken)));
        api.MapPost("/projects/{projectId:guid}/memories", async (Guid projectId, ReportMemoryRequest request, ClaimsPrincipal user, MemoryApplication application, CancellationToken cancellationToken) =>
        {
            var result = await application.ReportAsync(projectId, request, ActorIdentity.FromClaims(user), cancellationToken);
            return Results.Created($"/v1/memories/{result.Memory.MemoryId}", result);
        });
        api.MapPost("/memories/{memoryId:guid}/versions", async (Guid memoryId, ReviseMemoryRequest request, ClaimsPrincipal user, MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Ok(await application.ReviseAsync(memoryId, request, ActorIdentity.FromClaims(user), cancellationToken)));
        api.MapGet("/memories/{memoryId:guid}", async (Guid memoryId, MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Ok(await application.GetAsync(memoryId, cancellationToken)));
        api.MapPost("/projects/{projectId:guid}/search", async (Guid projectId, SearchMemoriesRequest request, MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Ok(await application.SearchAsync(projectId, request.Query, request.Type, request.Limit, cancellationToken)));
        api.MapPut("/memories/{memoryId:guid}/versions/{version:int}/feedback", async (Guid memoryId, int version, MemoryFeedbackRequest request, ClaimsPrincipal user, MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Ok(await application.SubmitFeedbackAsync(memoryId, version, ActorIdentity.FromClaims(user), request, cancellationToken)));
        api.MapDelete("/memories/{memoryId:guid}/versions/{version:int}/feedback", async (Guid memoryId, int version, ClaimsPrincipal user, MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Ok(await application.DeleteFeedbackAsync(memoryId, version, ActorIdentity.FromClaims(user), cancellationToken)));
        api.MapGet("/memories/{memoryId:guid}/versions/{version:int}/feedback-summary", async (Guid memoryId, int version, MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Ok(await application.GetFeedbackSummaryAsync(memoryId, version, cancellationToken)));

        endpoints.MapPost("/internal/reindex", async (MemoryApplication application, CancellationToken cancellationToken) =>
            Results.Ok(await application.ReindexAsync(cancellationToken)))
            .RequireAuthorization();
        return endpoints;
    }
}

public sealed record SearchMemoriesRequest(string Query, string? Type = null, int Limit = 10);