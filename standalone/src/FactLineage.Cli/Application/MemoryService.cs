using System.Text.Json;
using FactLineage.Cli.Domain;
using FactLineage.Cli.Infrastructure;
using Microsoft.Data.Sqlite;

namespace FactLineage.Cli.Application;

public sealed class MemoryService(SqliteDatabase database, ProjectService projects, GitInspector gitInspector, IEmbeddingProvider? embeddingProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IEmbeddingProvider _embeddingProvider = embeddingProvider ?? new DisabledEmbeddingProvider();

    public MemoryVersion Report(string projectName, MemoryReportRequest request, bool allowMissingReferences = false)
    {
        ValidateRequest(request.Type, request.Title, request.Summary, request.CodeReferences, request.CreatedBy);
        var project = projects.Get(projectName);
        ValidateReferences(project.RepositoryPath, request.CodeReferences, allowMissingReferences);
        var memory = new Memory(Guid.NewGuid().ToString(), project.Id, request.Type, request.Title.Trim(), 1, DateTimeOffset.UtcNow);
        var version = CreateVersion(memory.Id, 1, memory.Title, request.Summary, request.Details, request.CodeReferences, request.CreatedBy, project.RepositoryPath, out var embedding);

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        InsertMemory(connection, transaction, memory);
        InsertVersion(connection, transaction, version, embedding);
        InsertSearchDocument(connection, transaction, memory, version);
        transaction.Commit();
        return version;
    }

    public MemoryVersion Revise(string memoryId, MemoryRevisionRequest request, bool allowMissingReferences = false)
    {
        ValidateRequest("feature", "revision", request.Summary, request.CodeReferences, request.CreatedBy);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var memory = GetMemory(connection, transaction, memoryId);
        var project = GetProject(connection, transaction, memory.ProjectId);
        ValidateReferences(project.RepositoryPath, request.CodeReferences, allowMissingReferences);
        var versionNumber = memory.CurrentVersion + 1;
        var version = CreateVersion(memory.Id, versionNumber, memory.Title, request.Summary, request.Details, request.CodeReferences, request.CreatedBy, project.RepositoryPath, out var embedding);
        InsertVersion(connection, transaction, version, embedding);

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE memories SET current_version = $version WHERE id = $id;";
        update.Parameters.AddWithValue("$version", versionNumber);
        update.Parameters.AddWithValue("$id", memoryId);
        update.ExecuteNonQuery();
        using var deleteSearch = connection.CreateCommand();
        deleteSearch.Transaction = transaction;
        deleteSearch.CommandText = "DELETE FROM memory_search WHERE memory_id = $memoryId;";
        deleteSearch.Parameters.AddWithValue("$memoryId", memoryId);
        deleteSearch.ExecuteNonQuery();
        InsertSearchDocument(connection, transaction, memory with { CurrentVersion = versionNumber }, version);
        transaction.Commit();
        return version;
    }

    public (Memory Memory, MemoryVersion Version) Get(string memoryId)
    {
        using var connection = database.OpenConnection();
        var memory = GetMemory(connection, null, memoryId);
        return (memory, GetVersion(connection, null, memoryId, memory.CurrentVersion));
    }

    public IReadOnlyList<MemoryVersion> History(string memoryId)
    {
        using var connection = database.OpenConnection();
        _ = GetMemory(connection, null, memoryId);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, memory_id, version, summary, details_json, code_references_json, commit_sha, created_by, created_at, embedding_model FROM memory_versions WHERE memory_id = $memoryId ORDER BY version;";
        command.Parameters.AddWithValue("$memoryId", memoryId);
        using var reader = command.ExecuteReader();
        var versions = new List<MemoryVersion>();
        while (reader.Read())
        {
            versions.Add(ReadVersion(reader));
        }

        return versions;
    }

    public IReadOnlyList<MemorySearchResult> Search(IReadOnlyList<string> projectNames, bool allProjects, string query, string? type = null, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new DomainException("INVALID_SEARCH_QUERY", "Search query is required.");
        }

        if (limit is < 1 or > 100)
        {
            throw new DomainException("INVALID_LIMIT", "Search limit must be between 1 and 100.");
        }

        if (projectNames.Count > 0 == allProjects)
        {
            throw new DomainException("PROJECT_SCOPE_REQUIRED", "Specify one or more --project values or --all-projects.");
        }

        var selectedProjects = allProjects ? projects.List() : projects.GetMany(projectNames);
        if (selectedProjects.Count == 0)
        {
            return [];
        }

        var keywordResults = SearchKeywords(selectedProjects, query, type);
        var queryEmbedding = TryCreateEmbedding(query, EmbeddingKind.Query);
        if (queryEmbedding is null)
        {
            return keywordResults.Take(limit).ToList();
        }

        var semanticResults = SearchSemantically(selectedProjects, type, queryEmbedding);
        if (semanticResults.Count == 0)
        {
            return keywordResults.Take(limit).ToList();
        }

        var maximumKeywordScore = keywordResults.Count == 0 ? 1d : keywordResults.Max(result => result.Score);
        var combined = new Dictionary<string, (MemorySearchResult Result, double Score)>();
        foreach (var result in keywordResults)
        {
            combined[result.Memory.Id] = (result, 0.4 * result.Score / maximumKeywordScore);
        }

        foreach (var (result, similarity) in semanticResults)
        {
            var semanticScore = 0.6 * Math.Max(0, similarity);
            if (combined.TryGetValue(result.Memory.Id, out var existing))
            {
                combined[result.Memory.Id] = (result, existing.Score + semanticScore);
            }
            else
            {
                combined[result.Memory.Id] = (result, semanticScore);
            }
        }

        return combined.Values
            .Select(item => item.Result with { Score = item.Score })
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Memory.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private IReadOnlyList<MemorySearchResult> SearchKeywords(IReadOnlyList<Project> selectedProjects, string query, string? type)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        var projectParameters = selectedProjects.Select((project, index) => $"$projectId{index}").ToArray();
        command.CommandText = """
            SELECT p.id, p.name, p.repository_path, p.remote_url, p.created_at, p.updated_at,
                   m.id, m.project_id, m.type, m.title, m.current_version, m.created_at,
                   v.id, v.memory_id, v.version, v.summary, v.details_json, v.code_references_json, v.commit_sha, v.created_by, v.created_at, v.embedding_model,
                   bm25(memory_search, 10.0, 3.0, 1.0, 2.0, 2.0) AS rank
            FROM memory_search
            JOIN memories m ON m.id = memory_search.memory_id
            JOIN memory_versions v ON v.memory_id = m.id AND v.version = m.current_version
            JOIN projects p ON p.id = m.project_id
            WHERE memory_search MATCH $query AND memory_search.project_id IN (PROJECT_IDS)
              AND ($type IS NULL OR m.type = $type)
            ORDER BY rank
            LIMIT 100;
            """.Replace("PROJECT_IDS", string.Join(", ", projectParameters), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$query", QuoteFtsQuery(query));
        foreach (var (project, index) in selectedProjects.Select((project, index) => (project, index))) command.Parameters.AddWithValue(projectParameters[index], project.Id);
        command.Parameters.AddWithValue("$type", (object?)type ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var results = new List<MemorySearchResult>();
        while (reader.Read())
        {
            results.Add(new MemorySearchResult(ReadProject(reader), ReadMemory(reader, 6), ReadVersion(reader, 12), -reader.GetDouble(22)));
        }

        return results;
    }

    private IReadOnlyList<(MemorySearchResult Result, double Similarity)> SearchSemantically(IReadOnlyList<Project> selectedProjects, string? type, Embedding queryEmbedding)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        var projectParameters = selectedProjects.Select((project, index) => $"$projectId{index}").ToArray();
        command.CommandText = """
            SELECT p.id, p.name, p.repository_path, p.remote_url, p.created_at, p.updated_at,
                   m.id, m.project_id, m.type, m.title, m.current_version, m.created_at,
                   v.id, v.memory_id, v.version, v.summary, v.details_json, v.code_references_json, v.commit_sha, v.created_by, v.created_at, v.embedding_model, v.embedding
            FROM memories m
            JOIN memory_versions v ON v.memory_id = m.id AND v.version = m.current_version
            JOIN projects p ON p.id = m.project_id
            WHERE m.project_id IN (PROJECT_IDS) AND ($type IS NULL OR m.type = $type)
              AND v.embedding IS NOT NULL AND v.embedding_model = $embeddingModel;
            """.Replace("PROJECT_IDS", string.Join(", ", projectParameters), StringComparison.Ordinal);
        foreach (var (project, index) in selectedProjects.Select((project, index) => (project, index))) command.Parameters.AddWithValue(projectParameters[index], project.Id);
        command.Parameters.AddWithValue("$type", (object?)type ?? DBNull.Value);
        command.Parameters.AddWithValue("$embeddingModel", queryEmbedding.Model);
        using var reader = command.ExecuteReader();
        var results = new List<(MemorySearchResult Result, double Similarity)>();
        while (reader.Read())
        {
            var vector = ToVector(reader.GetFieldValue<byte[]>(22));
            if (vector.Length != queryEmbedding.Vector.Length)
            {
                continue;
            }

            results.Add((new MemorySearchResult(ReadProject(reader), ReadMemory(reader, 6), ReadVersion(reader, 12), 0), CosineSimilarity(queryEmbedding.Vector, vector)));
        }

        return results;
    }

    private MemoryVersion CreateVersion(string memoryId, int version, string title, string summary, object? details, IReadOnlyList<CodeReference> references, string createdBy, string repositoryPath, out Embedding? embedding)
    {
        var gitState = gitInspector.Inspect(repositoryPath);
        var detailsJson = JsonSerializer.Serialize(details, JsonOptions);
        var referencesJson = JsonSerializer.Serialize(references, JsonOptions);
        embedding = TryCreateEmbedding(EmbeddingDocument.Create(title, summary, detailsJson, referencesJson), EmbeddingKind.Document);
        return new MemoryVersion(Guid.NewGuid().ToString(), memoryId, version, summary.Trim(), detailsJson, referencesJson, gitState.CommitSha, createdBy.Trim(), DateTimeOffset.UtcNow, embedding?.Model);
    }

    private static void ValidateRequest(string type, string title, string summary, IReadOnlyList<CodeReference> references, string createdBy)
    {
        if (type is not ("feature" or "api" or "decision")) throw new DomainException("INVALID_MEMORY_TYPE", "Memory type must be feature, api, or decision.");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("INVALID_MEMORY_TITLE", "Memory title is required.");
        if (string.IsNullOrWhiteSpace(summary)) throw new DomainException("INVALID_MEMORY_SUMMARY", "Memory summary is required.");
        if (string.IsNullOrWhiteSpace(createdBy)) throw new DomainException("INVALID_CREATED_BY", "CreatedBy is required.");
        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.Path) || reference.StartLine < 1 || reference.EndLine < reference.StartLine)
                throw new DomainException("INVALID_CODE_REFERENCE", "Code references must have a path and valid line range.");
        }
    }

    private Embedding? TryCreateEmbedding(string text, EmbeddingKind kind)
    {
        try
        {
            return _embeddingProvider.Create(text, kind);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ValidateReferences(string repositoryPath, IReadOnlyList<CodeReference> references, bool allowMissingReferences)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)) + Path.DirectorySeparatorChar;
        foreach (var reference in references)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, reference.Path));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new DomainException("CODE_REFERENCE_OUTSIDE_PROJECT", "Code reference path must be inside the project root.");
            if (!allowMissingReferences && !File.Exists(fullPath))
                throw new DomainException("CODE_REFERENCE_NOT_FOUND", $"Code reference '{reference.Path}' does not exist.");
        }
    }

    private static string QuoteFtsQuery(string query) => $"\"{query.Replace("\"", "\"\"")}\"";

    private static void InsertMemory(SqliteConnection connection, SqliteTransaction transaction, Memory memory)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO memories (id, project_id, type, title, current_version, created_at) VALUES ($id, $projectId, $type, $title, $currentVersion, $createdAt);";
        command.Parameters.AddWithValue("$id", memory.Id); command.Parameters.AddWithValue("$projectId", memory.ProjectId); command.Parameters.AddWithValue("$type", memory.Type); command.Parameters.AddWithValue("$title", memory.Title); command.Parameters.AddWithValue("$currentVersion", memory.CurrentVersion); command.Parameters.AddWithValue("$createdAt", memory.CreatedAt.ToString("O")); command.ExecuteNonQuery();
    }

    private static void InsertVersion(SqliteConnection connection, SqliteTransaction transaction, MemoryVersion version, Embedding? embedding)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO memory_versions (id, memory_id, version, summary, details_json, code_references_json, commit_sha, embedding, embedding_model, created_by, created_at) VALUES ($id, $memoryId, $version, $summary, $details, $references, $commitSha, $embedding, $embeddingModel, $createdBy, $createdAt);";
        command.Parameters.AddWithValue("$id", version.Id); command.Parameters.AddWithValue("$memoryId", version.MemoryId); command.Parameters.AddWithValue("$version", version.Version); command.Parameters.AddWithValue("$summary", version.Summary); command.Parameters.AddWithValue("$details", version.DetailsJson); command.Parameters.AddWithValue("$references", version.CodeReferencesJson); command.Parameters.AddWithValue("$commitSha", (object?)version.CommitSha ?? DBNull.Value); command.Parameters.AddWithValue("$embedding", embedding is null ? DBNull.Value : ToBytes(embedding.Vector)); command.Parameters.AddWithValue("$embeddingModel", (object?)version.EmbeddingModel ?? DBNull.Value); command.Parameters.AddWithValue("$createdBy", version.CreatedBy); command.Parameters.AddWithValue("$createdAt", version.CreatedAt.ToString("O")); command.ExecuteNonQuery();
    }

    private static void InsertSearchDocument(SqliteConnection connection, SqliteTransaction transaction, Memory memory, MemoryVersion version)
    {
        var references = JsonSerializer.Deserialize<List<CodeReference>>(version.CodeReferencesJson, JsonOptions) ?? [];
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO memory_search (title, summary, details, paths, symbols, project_id, memory_id, version) VALUES ($title, $summary, $details, $paths, $symbols, $projectId, $memoryId, $version);";
        command.Parameters.AddWithValue("$title", memory.Title); command.Parameters.AddWithValue("$summary", version.Summary); command.Parameters.AddWithValue("$details", version.DetailsJson); command.Parameters.AddWithValue("$paths", string.Join(' ', references.Select(item => item.Path))); command.Parameters.AddWithValue("$symbols", string.Join(' ', references.Where(item => item.Symbol is not null).Select(item => item.Symbol))); command.Parameters.AddWithValue("$projectId", memory.ProjectId); command.Parameters.AddWithValue("$memoryId", memory.Id); command.Parameters.AddWithValue("$version", version.Version); command.ExecuteNonQuery();
    }

    private static Memory GetMemory(SqliteConnection connection, SqliteTransaction? transaction, string memoryId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id, project_id, type, title, current_version, created_at FROM memories WHERE id = $id;"; command.Parameters.AddWithValue("$id", memoryId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new DomainException("MEMORY_NOT_FOUND", $"Memory '{memoryId}' does not exist.");
        return ReadMemory(reader);
    }

    private static Project GetProject(SqliteConnection connection, SqliteTransaction transaction, string projectId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id, name, repository_path, remote_url, created_at, updated_at FROM projects WHERE id = $id;"; command.Parameters.AddWithValue("$id", projectId);
        using var reader = command.ExecuteReader(); reader.Read();
        return new Project(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)));
    }

    private static MemoryVersion GetVersion(SqliteConnection connection, SqliteTransaction? transaction, string memoryId, int version)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id, memory_id, version, summary, details_json, code_references_json, commit_sha, created_by, created_at, embedding_model FROM memory_versions WHERE memory_id = $memoryId AND version = $version;"; command.Parameters.AddWithValue("$memoryId", memoryId); command.Parameters.AddWithValue("$version", version);
        using var reader = command.ExecuteReader(); reader.Read(); return ReadVersion(reader);
    }

    private static Project ReadProject(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)));
    private static Memory ReadMemory(SqliteDataReader reader, int offset = 0) => new(reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetInt32(offset + 4), DateTimeOffset.Parse(reader.GetString(offset + 5)));
    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ToVector(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            return [];
        }

        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static double CosineSimilarity(float[] first, float[] second)
    {
        var dotProduct = 0d;
        var firstNorm = 0d;
        var secondNorm = 0d;
        for (var index = 0; index < first.Length; index++)
        {
            dotProduct += first[index] * second[index];
            firstNorm += first[index] * first[index];
            secondNorm += second[index] * second[index];
        }

        return firstNorm == 0 || secondNorm == 0 ? 0 : dotProduct / Math.Sqrt(firstNorm * secondNorm);
    }

    private static MemoryVersion ReadVersion(SqliteDataReader reader, int offset = 0) => new(reader.GetString(offset), reader.GetString(offset + 1), reader.GetInt32(offset + 2), reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetString(offset + 5), reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6), reader.GetString(offset + 7), DateTimeOffset.Parse(reader.GetString(offset + 8)), reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9));
}