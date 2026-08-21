using FactLineage.Cli.Domain;
using FactLineage.Cli.Infrastructure;
using Microsoft.Data.Sqlite;

namespace FactLineage.Cli.Application;

public sealed class ProjectService(SqliteDatabase database)
{
    public Project Add(CreateProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new DomainException("INVALID_PROJECT_NAME", "Project name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryPath) || !Directory.Exists(request.RepositoryPath))
        {
            throw new DomainException("INVALID_PROJECT_PATH", "Project path must be an existing directory.");
        }

        var project = new Project(
            Guid.NewGuid().ToString(),
            request.Name.Trim(),
            Path.GetFullPath(request.RepositoryPath),
            request.RemoteUrl,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO projects (id, name, repository_path, remote_url, created_at, updated_at)
                VALUES ($id, $name, $repositoryPath, $remoteUrl, $createdAt, $updatedAt);
                """;
            command.Parameters.AddWithValue("$id", project.Id);
            command.Parameters.AddWithValue("$name", project.Name);
            command.Parameters.AddWithValue("$repositoryPath", project.RepositoryPath);
            command.Parameters.AddWithValue("$remoteUrl", (object?)project.RemoteUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", project.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", project.UpdatedAt.ToString("O"));
            command.ExecuteNonQuery();
            return project;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new DomainException("PROJECT_ALREADY_EXISTS", $"Project '{project.Name}' already exists.");
        }
    }

    public IReadOnlyList<Project> List()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, repository_path, remote_url, created_at, updated_at FROM projects ORDER BY name;";
        using var reader = command.ExecuteReader();
        var projects = new List<Project>();
        while (reader.Read())
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public IReadOnlyList<Project> GetMany(IReadOnlyList<string> names)
    {
        var projects = new List<Project>();
        foreach (var name in names)
        {
            if (projects.Any(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            projects.Add(Get(name));
        }

        return projects;
    }

    public Project Get(string name)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, repository_path, remote_url, created_at, updated_at FROM projects WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new DomainException("PROJECT_NOT_FOUND", $"Project '{name}' does not exist.");
        }

        return ReadProject(reader);
    }

    public Project Update(string name, UpdateProjectRequest request)
    {
        if (request.ClearRemoteUrl && request.RemoteUrl is not null)
        {
            throw new DomainException("INVALID_PROJECT_UPDATE", "--clear-remote-url cannot be used with --remote-url.");
        }

        if (request.NewName is null && request.RepositoryPath is null && request.RemoteUrl is null && !request.ClearRemoteUrl)
        {
            throw new DomainException("INVALID_PROJECT_UPDATE", "Specify at least one project field to update.");
        }

        if (request.NewName is { } newName && string.IsNullOrWhiteSpace(newName))
        {
            throw new DomainException("INVALID_PROJECT_NAME", "Project name is required.");
        }

        if (request.RepositoryPath is { } repositoryPath && !Directory.Exists(repositoryPath))
        {
            throw new DomainException("INVALID_PROJECT_PATH", "Project path must be an existing directory.");
        }

        var existing = Get(name);
        var updatedAt = DateTimeOffset.UtcNow;
        try
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE projects
                SET name = COALESCE($newName, name),
                    repository_path = COALESCE($repositoryPath, repository_path),
                    remote_url = CASE WHEN $clearRemoteUrl THEN NULL WHEN $remoteUrl IS NOT NULL THEN $remoteUrl ELSE remote_url END,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$newName", (object?)request.NewName?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("$repositoryPath", (object?)(request.RepositoryPath is null ? null : Path.GetFullPath(request.RepositoryPath)) ?? DBNull.Value);
            command.Parameters.AddWithValue("$remoteUrl", (object?)request.RemoteUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$clearRemoteUrl", request.ClearRemoteUrl);
            command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
            command.Parameters.AddWithValue("$id", existing.Id);
            command.ExecuteNonQuery();
            return Get(request.NewName?.Trim() ?? existing.Name);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new DomainException("PROJECT_ALREADY_EXISTS", "A project with the requested name or path already exists.");
        }
    }

    public ProjectRemovalResult Remove(string name)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var project = GetProject(connection, transaction, name);
        var memoriesRemoved = Count(connection, transaction, "SELECT COUNT(*) FROM memories WHERE project_id = $projectId;", project.Id);
        var versionsRemoved = Count(connection, transaction, "SELECT COUNT(*) FROM memory_versions WHERE memory_id IN (SELECT id FROM memories WHERE project_id = $projectId);", project.Id);
        var searchDocumentsRemoved = Count(connection, transaction, "SELECT COUNT(*) FROM memory_search WHERE project_id = $projectId;", project.Id);
        Execute(connection, transaction, "DELETE FROM memory_search WHERE project_id = $projectId;", project.Id);
        Execute(connection, transaction, "DELETE FROM projects WHERE id = $projectId;", project.Id);
        transaction.Commit();
        return new ProjectRemovalResult(project, memoriesRemoved, versionsRemoved, searchDocumentsRemoved);
    }

    private static Project GetProject(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, repository_path, remote_url, created_at, updated_at FROM projects WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new DomainException("PROJECT_NOT_FOUND", $"Project '{name}' does not exist.");
        return ReadProject(reader);
    }

    private static int Count(SqliteConnection connection, SqliteTransaction transaction, string sql, string projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$projectId", projectId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, string projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.ExecuteNonQuery();
    }

    private static Project ReadProject(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4)),
        DateTimeOffset.Parse(reader.GetString(5)));
}