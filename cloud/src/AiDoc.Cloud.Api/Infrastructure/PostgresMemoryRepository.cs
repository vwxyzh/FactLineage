using System.Text.Json;
using AiDoc.Cloud.Api.Application;
using AiDoc.Cloud.Api.Domain;
using Npgsql;

namespace AiDoc.Cloud.Api.Infrastructure;

public sealed class PostgresMemoryRepository(NpgsqlDataSource dataSource) : IMemoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string CurrentMemoryColumns = """
        SELECT m.id, m.project_id, m.type, m.title, v.version, v.summary,
               v.details::text, v.code_references::text, v.content_text, v.created_by, v.created_at
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
                created_at TIMESTAMPTZ NOT NULL,
                UNIQUE(memory_id, version)
            );
            CREATE INDEX IF NOT EXISTS ix_memories_project_type ON memories(project_id, type);
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

    public async Task<MemoryRecord> CreateMemoryAsync(Guid projectId, ReportMemoryRequest request, string contentText, CancellationToken cancellationToken)
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

        await InsertVersionAsync(connection, transaction, memoryId, 1, request.Summary, request.Details, request.CodeReferences, contentText, request.CreatedBy, createdAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MemoryRecord(memoryId, projectId, request.Type, request.Title, 1, request.Summary, request.Details, request.CodeReferences, contentText, request.CreatedBy, createdAt);
    }

    public async Task<MemoryRecord> ReviseMemoryAsync(Guid memoryId, ReviseMemoryRequest request, string contentText, CancellationToken cancellationToken)
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
        await InsertVersionAsync(connection, transaction, memoryId, version, request.Summary, request.Details, request.CodeReferences, contentText, request.CreatedBy, createdAt, cancellationToken);
        await using (var update = new NpgsqlCommand("UPDATE memories SET current_version = $1 WHERE id = $2;", connection, transaction))
        {
            update.Parameters.AddWithValue(version);
            update.Parameters.AddWithValue(memoryId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MemoryRecord(memoryId, projectId, type, title, version, request.Summary, request.Details, request.CodeReferences, contentText, request.CreatedBy, createdAt);
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

    private static async Task EnsureProjectExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid projectId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM projects WHERE id = $1);", connection, transaction);
        command.Parameters.AddWithValue(projectId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new DomainException("PROJECT_NOT_FOUND", $"Project '{projectId}' was not found.");
        }
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
        string createdBy,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO memory_versions
                (id, memory_id, version, summary, details, code_references, content_text, created_by, created_at)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7, $8, $9);
            """, connection, transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(memoryId);
        command.Parameters.AddWithValue(version);
        command.Parameters.AddWithValue(summary);
        command.Parameters.AddWithValue(details?.GetRawText() ?? "null");
        command.Parameters.AddWithValue(JsonSerializer.Serialize(references, JsonOptions));
        command.Parameters.AddWithValue(contentText);
        command.Parameters.AddWithValue(createdBy);
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
            reader.GetFieldValue<DateTimeOffset>(10));
    }
}