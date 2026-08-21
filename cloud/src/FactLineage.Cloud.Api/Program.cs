using Azure.Core;
using Azure.Identity;
using FactLineage.Cloud.Api.Api;
using FactLineage.Cloud.Api.Application;
using FactLineage.Cloud.Api.Configuration;
using FactLineage.Cloud.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services
	.AddOptions<CloudOptions>()
	.Bind(builder.Configuration.GetSection(CloudOptions.SectionName))
	.ValidateDataAnnotations()
	.Validate(
		options => options.ApiAudience.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
			&& Guid.TryParse(options.ApiAudience["api://".Length..], out _),
		"Cloud:ApiAudience must use the format api://<application-client-id>.")
	.ValidateOnStart();
builder.Services.AddSingleton<TokenCredential>(services =>
{
	var clientId = services.GetRequiredService<IOptions<CloudOptions>>().Value.ManagedIdentityClientId;
	return new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });
});
builder.Services
	.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
	.AddJwtBearer(options =>
	{
		var cloud = builder.Configuration.GetSection(CloudOptions.SectionName).Get<CloudOptions>() ?? new CloudOptions();
		options.Authority = $"https://login.microsoftonline.com/{cloud.TenantId}/v2.0";
		options.TokenValidationParameters = new TokenValidationParameters
		{
			ValidAudiences = [cloud.ApiAudience, cloud.ApiAudience.Replace("api://", string.Empty, StringComparison.OrdinalIgnoreCase)]
		};
	});
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton<NpgsqlDataSource>(PostgresDataSourceFactory.Create);
builder.Services.AddSingleton<IMemoryRepository, PostgresMemoryRepository>();
builder.Services.AddSingleton<IEmbeddingService, AzureOpenAiEmbeddingService>();
builder.Services.AddSingleton<IMemorySearchIndex, AzureSearchMemoryIndex>();
builder.Services.AddSingleton<MemoryApplication>();
builder.Services.AddHostedService<CloudInitializer>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<MemoryTools>();
builder.Services.AddHealthChecks();

var app = builder.Build();
var documentationContentTypes = new FileExtensionContentTypeProvider();
documentationContentTypes.Mappings[".md"] = "text/markdown; charset=utf-8";
documentationContentTypes.Mappings[".txt"] = "text/plain; charset=utf-8";

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
	ContentTypeProvider = documentationContentTypes,
	OnPrepareResponse = context =>
	{
		context.Context.Response.Headers.CacheControl = "public, max-age=300";
		context.Context.Response.Headers.XContentTypeOptions = "nosniff";
	}
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", () => Results.Redirect("/docs/"));
app.MapGet(McpDiscoveryDocument.DiscoveryPath, (HttpRequest request, IOptions<CloudOptions> options) =>
	Results.Ok(McpDiscoveryDocument.Create(options.Value, $"https://{request.Host}")));
app.MapGet(McpDiscoveryDocument.LegacyDiscoveryPath, (HttpRequest request, IOptions<CloudOptions> options) =>
	Results.Ok(McpDiscoveryDocument.Create(options.Value, $"https://{request.Host}")));
app.MapHealthChecks("/health");
app.MapFactLineageApi();
app.MapMcp("/mcp").RequireAuthorization();

app.Run();

public partial class Program;