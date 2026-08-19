using Microsoft.Data.Sqlite;

namespace AiDoc.Cli.Infrastructure;

public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    public void Migrate()
    {
        using var connection = OpenConnection();
        var statements = new[]
        {
            "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);",
            "CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE, repository_path TEXT NOT NULL COLLATE NOCASE UNIQUE, remote_url TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);",
            "CREATE TABLE IF NOT EXISTS memories (id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE, type TEXT NOT NULL CHECK(type IN ('feature', 'api', 'decision')), title TEXT NOT NULL, current_version INTEGER NOT NULL, created_at TEXT NOT NULL);",
            "CREATE TABLE IF NOT EXISTS memory_versions (id TEXT PRIMARY KEY, memory_id TEXT NOT NULL REFERENCES memories(id) ON DELETE CASCADE, version INTEGER NOT NULL, summary TEXT NOT NULL, details_json TEXT NOT NULL, code_references_json TEXT NOT NULL, commit_sha TEXT NULL, embedding BLOB NULL, embedding_model TEXT NULL, created_by TEXT NOT NULL, created_at TEXT NOT NULL, UNIQUE(memory_id, version));",
            "CREATE VIRTUAL TABLE IF NOT EXISTS memory_search USING fts5(title, summary, details, paths, symbols, project_id UNINDEXED, memory_id UNINDEXED, version UNINDEXED, tokenize = 'trigram');",
            "INSERT OR IGNORE INTO schema_migrations (version, applied_at) VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));"
        };
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}