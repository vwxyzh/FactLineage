using System.Text.Json;
using FactLineage.Cloud.Api.Application;
using FactLineage.Cloud.Api.Domain;
using Npgsql;

namespace FactLineage.Cloud.Api.Infrastructure;

public sealed class PostgresMemoryRepository(NpgsqlDataSource dataSource) : IMemoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string CurrentMemoryColumns = """
        SELECT m.id, m.project_id, m.type, m.title, v.version, v.summary,
             v.details::text, v.code_references::text, v.content_text, v.created_by, v.created_at, v.actor_id
        FROM memories m
        JOIN memory_versions v ON v.memory_id = m.id AND v.version = m.current_version
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS projects (
                id UUID PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                repository_url TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL
            );
            CREATE TABLE IF NOT EXISTS memories (
                id UUID PRIMARY KEY,
                project_id UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                type TEXT NOT NULL,
                title TEXT NOT NULL,
                current_version INTEGER NOT NULL,
                created_at TIMESTAMPTZ NOT NULL
            );
            CREATE TABLE IF NOT EXISTS memory_versions (
                id UUID PRIMARY KEY,
                memory_id UUID NOT NULL REFERENCES memories(id) ON DELETE CASCADE,
                version INTEGER NOT NULL,
                summary TEXT NOT NULL,
                details JSONB NOT NULL,
                code_references JSONB NOT NULL,
                content_text TEXT NOT NULL,
                created_by TEXT NOT NULL,
                actor_id TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                UNIQUE(memory_id, version)
            );
            ALTER TABLE memory_versions ADD COLUMN IF NOT EXISTS actor_id TEXT NULL;
            CREATE TABLE IF NOT EXISTS memory_feedback (
                id UUID PRIMARY KEY,
                memory_version_id UUID NOT NULL REFERENCES memory_versions(id) ON DELETE CASCADE,
                actor_id TEXT NOT NULL,
                sentiment TEXT NOT NULL CHECK (sentiment IN ('useful', 'not_useful')),
                reason TEXT NOT NULL CHECK (reason IN ('useful', 'incorrect', 'stale', 'irrelevant', 'missing_evidence')),
                comment TEXT NULL,
                search_query TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL,
                CHECK ((sentiment = 'useful' AND reason = 'useful') OR
                       (sentiment = 'not_useful' AND reason IN ('incorrect', 'stale', 'irrelevant', 'missing_evidence'))),
                UNIQUE(memory_version_id, actor_id)
            );
            CREATE INDEX IF NOT EXISTS ix_memories_project_type ON memories(project_id, type);
            CREATE INDEX IF NOT EXISTS ix_memory_feedback_version_reason ON memory_feedback(memory_version_id, reason);
            """;
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProjectRecord> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = new ProjectRecord(Guid.NewGuid(), request.Name, request.RepositoryUrl, DateTimeOffset.UtcNow);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO projects (id, name, repository_url, created_at)
            VALUES ($1, $2, $3, $4);
            """);
        command.Parameters.AddWithValue(project.Id);
        command.Parameters.AddWithValue(project.Name);
        command.Parameters.AddWithValue((object?)project.RepositoryUrl ?? DBNull.Value);
        command.Parameters.AddWithValue(project.CreatedAt);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DomainException("PROJECT_ALREADY_EXISTS", $"Project '{request.Name}' already exists.");
        }

        return project;
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id, name, repository_url, created_at FROM projects ORDER BY name;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var projects = new List<ProjectRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(new ProjectRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return projects;
    }

    public async Task<MemoryRecord> CreateMemoryAsync(Guid projectId, ReportMemoryRequest request, string contentText, string actorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureProjectExistsAsync(connection, transaction, projectId, cancellationToken);
        var memoryId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        await using (var memoryCommand = new NpgsqlCommand("""
            INSERT INTO memories (id, project_id, type, title, current_version, created_at)
            VALUES ($1, $2, $3, $4, 1, $5);
            """, connection, transaction))
        {
            memoryCommand.Parameters.AddWithValue(memoryId);
            memoryCommand.Parameters.AddWithValue(projectId);
            memoryCommand.Parameters.AddWithValue(request.Type);
            memoryCommand.Parameters.AddWithValue(request.Title);
            memoryCommand.Parameters.AddWithValue(createdAt);
            await memoryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertVersionAsync(connection, transaction, memoryId, 1, request.Summary, request.Details, request.CodeReferences, contentText, request.AgentName!, actorId, createdAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MemoryRecord(memoryId, projectId, request.Type, request.Title, 1, request.Summary, request.Details, request.CodeReferences, contentText, request.AgentName!, createdAt, actorId);
    }

    public async Task<MemoryRecord> ReviseMemoryAsync(Guid memoryId, ReviseMemoryRequest request, string contentText, string actorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid projectId;
        string type;
        string title;
        int version;
        await using (var read = new NpgsqlCommand("SELECT project_id, type, title, current_version FROM memories WHERE id = $1 FOR UPDATE;", connection, transaction))
        {
            read.Parameters.AddWithValue(memoryId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new DomainException("MEMORY_NOT_FOUND", $"Memory '{memoryId}' was not found.");
            }

            projectId = reader.GetGuid(0);
            type = reader.GetString(1);
            title = reader.GetString(2);
            version = reader.GetInt32(3) + 1;
        }

        var createdAt = DateTimeOffset.UtcNow;
        await InsertVersionAsync(connection, transaction, memoryId, version, request.Summary, request.Details, request.CodeReferences, contentText, request.AgentName!, actorId, createdAt, cancellationToken);
        await using (var update = new NpgsqlCommand("UPDATE memories SET current_version = $1 WHERE id = $2;", connection, transaction))
        {
            update.Parameters.AddWithValue(version);
            update.Parameters.AddWithValue(memoryId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MemoryRecord(memoryId, projectId, type, title, version, request.Summary, request.Details, request.CodeReferences, contentText, request.AgentName!, createdAt, actorId);
    }

    public async Task<MemoryRecord?> GetMemoryAsync(Guid memoryId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"{CurrentMemoryColumns} WHERE m.id = $1;");
        command.Parameters.AddWithValue(memoryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMemory(reader) : null;
    }

    public async Task<IReadOnlyList<MemoryRecord>> GetMemoriesAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0) return [];
        await using var command = dataSource.CreateCommand($"{CurrentMemoryColumns} WHERE m.id = ANY($1);");
        command.Parameters.AddWithValue(memoryIds.ToArray());
        return await ReadMemoriesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListCurrentMemoriesAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(CurrentMemoryColumns);
        return await ReadMemoriesAsync(command, cancellationToken);
    }

    public async Task<MemoryFeedbackResult> UpsertFeedbackAsync(Guid memoryId, int version, string actorId, MemoryFeedbackRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var versionId = await GetVersionIdAsync(connection, transaction, memoryId, version, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO memory_feedback
                (id, memory_version_id, actor_id, sentiment, reason, comment, search_query, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $8)
            ON CONFLICT (memory_version_id, actor_id) DO UPDATE SET
                sentiment = EXCLUDED.sentiment,
                reason = EXCLUDED.reason,
                comment = EXCLUDED.comment,
                search_query = EXCLUDED.search_query,
                updated_at = EXCLUDED.updated_at;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(versionId);
            command.Parameters.AddWithValue(actorId);
            command.Parameters.AddWithValue(request.Sentiment);
            command.Parameters.AddWithValue(request.Reason);
            command.Parameters.AddWithValue((object?)request.Comment ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)request.SearchQuery ?? DBNull.Value);
            command.Parameters.AddWithValue(now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var summary = await GetFeedbackSummaryAsync(connection, transaction, memoryId, version, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MemoryFeedbackResult(memoryId, version, request.Sentiment, request.Reason, request.Comment, now, summary);
    }

    public async Task<MemoryFeedbackSummary> DeleteFeedbackAsync(Guid memoryId, int version, string actorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var versionId = await GetVersionIdAsync(connection, transaction, memoryId, version, cancellationToken);
        await using (var command = new NpgsqlCommand("DELETE FROM memory_feedback WHERE memory_version_id = $1 AND actor_id = $2;", connection, transaction))
        {
            command.Parameters.AddWithValue(versionId);
            command.Parameters.AddWithValue(actorId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var summary = await GetFeedbackSummaryAsync(connection, transaction, memoryId, version, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return summary;
    }

    public async Task<MemoryFeedbackSummary> GetFeedbackSummaryAsync(Guid memoryId, int version, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetFeedbackSummaryAsync(connection, null, memoryId, version, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, MemoryFeedbackSummary>> GetCurrentFeedbackSummariesAsync(IReadOnlyList<Guid> memoryIds, CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0) return new Dictionary<Guid, MemoryFeedbackSummary>();
        await using var command = dataSource.CreateCommand("""
            SELECT m.id, v.version,
                   (COUNT(f.id) FILTER (WHERE f.sentiment = 'useful'))::int,
                   (COUNT(f.id) FILTER (WHERE f.sentiment = 'not_useful'))::int,
                   (COUNT(f.id) FILTER (WHERE f.reason = 'incorrect'))::int,
                   (COUNT(f.id) FILTER (WHERE f.reason = 'stale'))::int,
                   (COUNT(f.id) FILTER (WHERE f.reason = 'irrelevant'))::int,
                   (COUNT(f.id) FILTER (WHERE f.reason = 'missing_evidence'))::int
            FROM memories m
            JOIN memory_versions v ON v.memory_id = m.id AND v.version = m.current_version
            LEFT JOIN memory_feedback f ON f.memory_version_id = v.id
            WHERE m.id = ANY($1)
            GROUP BY m.id, v.version;
            """);
        command.Parameters.AddWithValue(memoryIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var summaries = new Dictionary<Guid, MemoryFeedbackSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = ReadFeedbackSummary(reader);
            summaries[summary.MemoryId] = summary;
        }

        return summaries;
    }

    private static async Task EnsureProjectExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM projects WHERE id = $1);", connection, transaction);
        command.Parameters.AddWithValue(projectId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new DomainException("PROJECT_NOT_FOUND", $"Project '{projectId}' was not found.");
        }
    }

    private static async Task<Guid> GetVersionIdAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid memoryId, int version, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM memory_versions WHERE memory_id = $1 AND version = $2;", connection, transaction);
        command.Parameters.AddWithValue(memoryId);
        command.Parameters.AddWithValue(version);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid versionId
            ? versionId
            : throw new DomainException("MEMORY_VERSION_NOT_FOUND", $"Memory '{memoryId}' version '{version}' was not found.");
    }

    private static async Task<MemoryFeedbackSummary> GetFeedbackSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid memoryId,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT v.memory_id, v.version,
                   (COUNT(f.id) FILTER (WHERE f.sentiment = 'useful'))::int,
                   (COUNT(f.id) FILTER (WHERE f.sentiment = 'not_useful'))::int,
                   (COUNT(f.id) FILTER (WHERE f.reason = 'incorrect'))::int,
                   (COUNT(f.id) FILTER (WHERE f.reason = 'stale'))::int,
                   (COUNT(f.id) FILTER (WHERE f.reason = 'irrelevant'))::int,
                   (COUNT(f.id) FILTER (WHERE f.reason = 'missing_evidence'))::int
            FROM memory_versions v
            LEFT JOIN memory_feedback f ON f.memory_version_id = v.id
            WHERE v.memory_id = $1 AND v.version = $2
            GROUP BY v.memory_id, v.version;
            """, connection, transaction);
        command.Parameters.AddWithValue(memoryId);
        command.Parameters.AddWithValue(version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new DomainException("MEMORY_VERSION_NOT_FOUND", $"Memory '{memoryId}' version '{version}' was not found.");
        }

        return ReadFeedbackSummary(reader);
    }

    private static MemoryFeedbackSummary ReadFeedbackSummary(NpgsqlDataReader reader)
    {
        var incorrectCount = reader.GetInt32(4);
        var staleCount = reader.GetInt32(5);
        var missingEvidenceCount = reader.GetInt32(7);
        return new MemoryFeedbackSummary(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            incorrectCount,
            staleCount,
            reader.GetInt32(6),
            missingEvidenceCount,
            incorrectCount + staleCount + missingEvidenceCount > 0);
    }

    private static async Task InsertVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid memoryId,
        int version,
        string summary,
        JsonElement? details,
        IReadOnlyList<CodeReference> references,
        string contentText,
        string agentName,
        string actorId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO memory_versions
                (id, memory_id, version, summary, details, code_references, content_text, created_by, actor_id, created_at)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7, $8, $9, $10);
            """, connection, transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(memoryId);
        command.Parameters.AddWithValue(version);
        command.Parameters.AddWithValue(summary);
        command.Parameters.AddWithValue(details?.GetRawText() ?? "null");
        command.Parameters.AddWithValue(JsonSerializer.Serialize(references, JsonOptions));
        command.Parameters.AddWithValue(contentText);
        command.Parameters.AddWithValue(agentName);
        command.Parameters.AddWithValue(actorId);
        command.Parameters.AddWithValue(createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<MemoryRecord>> ReadMemoriesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var memories = new List<MemoryRecord>();
        while (await reader.ReadAsync(cancellationToken)) memories.Add(ReadMemory(reader));
        return memories;
    }

    private static MemoryRecord ReadMemory(NpgsqlDataReader reader)
    {
        using var details = JsonDocument.Parse(reader.GetString(6));
        var references = JsonSerializer.Deserialize<List<CodeReference>>(reader.GetString(7), JsonOptions) ?? [];
        return new MemoryRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            details.RootElement.ValueKind == JsonValueKind.Null ? null : details.RootElement.Clone(),
            references,
            reader.GetString(8),
            reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }
}