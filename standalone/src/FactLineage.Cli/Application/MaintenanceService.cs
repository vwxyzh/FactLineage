using FactLineage.Cli.Infrastructure;
using Microsoft.Data.Sqlite;

namespace FactLineage.Cli.Application;

public sealed class MaintenanceService(SqliteDatabase database, ProjectService projects, IEmbeddingProvider? embeddingProvider = null)
{
    private readonly IEmbeddingProvider _embeddingProvider = embeddingProvider ?? new DisabledEmbeddingProvider();

    public DoctorResult Doctor()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var integrityCheck = command.ExecuteScalar()?.ToString() == "ok";
        var missingProjectPaths = projects.List().Where(project => !Directory.Exists(project.RepositoryPath)).Select(project => project.Name).ToList();
        return new DoctorResult(integrityCheck, missingProjectPaths, _embeddingProvider.IsAvailable ? _embeddingProvider.Model : "pending");
    }

    public string Backup(string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var path = Path.Combine(backupDirectory, $"factlineage-v1-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.db");
        using var source = database.OpenConnection();
        using var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        destination.Open();
        source.BackupDatabase(destination);
        return path;
    }

    public EmbeddingBackfillResult Backfill(string projectName)
    {
        if (!_embeddingProvider.IsAvailable)
        {
            throw new Domain.DomainException("EMBEDDING_MODEL_UNAVAILABLE", "Download the local embedding model before running backfill.");
        }

        var project = projects.Get(projectName);
        var versions = GetVersionsWithoutCurrentModel(project.Id);
        var updates = versions
            .Select(version => new { version.Id, Embedding = _embeddingProvider.Create(EmbeddingDocument.Create(version.Title, version.Summary, version.DetailsJson, version.CodeReferencesJson), EmbeddingKind.Document) })
            .Where(item => item.Embedding is not null)
            .ToList();

        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var update in updates)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE memory_versions SET embedding = $embedding, embedding_model = $embeddingModel WHERE id = $id;";
            command.Parameters.AddWithValue("$embedding", ToBytes(update.Embedding!.Vector));
            command.Parameters.AddWithValue("$embeddingModel", update.Embedding.Model);
            command.Parameters.AddWithValue("$id", update.Id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return new EmbeddingBackfillResult(versions.Count, updates.Count, _embeddingProvider.Model);
    }

    private IReadOnlyList<VersionForEmbedding> GetVersionsWithoutCurrentModel(string projectId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id, m.title, v.summary, v.details_json, v.code_references_json
            FROM memory_versions v
            JOIN memories m ON m.id = v.memory_id
            WHERE m.project_id = $projectId AND (v.embedding IS NULL OR v.embedding_model <> $embeddingModel);
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$embeddingModel", _embeddingProvider.Model);
        using var reader = command.ExecuteReader();
        var versions = new List<VersionForEmbedding>();
        while (reader.Read())
        {
            versions.Add(new VersionForEmbedding(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return versions;
    }

    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}

public sealed record DoctorResult(bool IntegrityCheckPassed, IReadOnlyList<string> MissingProjectPaths, string EmbeddingProvider);

public sealed record EmbeddingBackfillResult(int Scanned, int Updated, string Model);

internal sealed record VersionForEmbedding(string Id, string Title, string Summary, string DetailsJson, string CodeReferencesJson);