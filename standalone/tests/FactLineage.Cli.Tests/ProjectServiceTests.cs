using FactLineage.Cli.Application;
using FactLineage.Cli.Domain;
using FactLineage.Cli.Infrastructure;
using Microsoft.Data.Sqlite;

namespace FactLineage.Cli.Tests;

public sealed class ProjectServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "factlineage-tests", Guid.NewGuid().ToString());

    [Fact]
    public void Add_PersistsAndListsProject()
    {
        Directory.CreateDirectory(_root);
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var database = new SqliteDatabase(Path.Combine(_root, "factlineage.db"));
        database.Migrate();
        var service = new ProjectService(database);

        var project = service.Add(new CreateProjectRequest("my-api", sourceDirectory));

        var stored = Assert.Single(service.List());
        Assert.Equal(project.Id, stored.Id);
        Assert.Equal("my-api", stored.Name);
        Assert.Equal(Path.GetFullPath(sourceDirectory), stored.RepositoryPath);
    }

    [Fact]
    public void Add_RejectsDuplicateName()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "factlineage.db"));
        database.Migrate();
        var service = new ProjectService(database);
        service.Add(new CreateProjectRequest("my-api", Directory.CreateDirectory(Path.Combine(_root, "first")).FullName));

        var exception = Assert.Throws<DomainException>(() => service.Add(
            new CreateProjectRequest("MY-API", Directory.CreateDirectory(Path.Combine(_root, "second")).FullName)));

        Assert.Equal("PROJECT_ALREADY_EXISTS", exception.Code);
    }

    [Fact]
    public void Update_ChangesMutableFieldsWithoutChangingProjectId()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "factlineage.db"));
        database.Migrate();
        var service = new ProjectService(database);
        var project = service.Add(new CreateProjectRequest("my-api", Directory.CreateDirectory(Path.Combine(_root, "first")).FullName, "https://example.test/first"));
        var newPath = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;

        var updated = service.Update("my-api", new UpdateProjectRequest("renamed-api", newPath, null, ClearRemoteUrl: true));

        Assert.Equal(project.Id, updated.Id);
        Assert.Equal("renamed-api", updated.Name);
        Assert.Equal(Path.GetFullPath(newPath), updated.RepositoryPath);
        Assert.Null(updated.RemoteUrl);
        Assert.True(updated.UpdatedAt >= project.CreatedAt);
    }

    [Fact]
    public void Migrate_CreatesFinalProjectsSchemaWithoutCompatibilityMigration()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "factlineage.db"));

        database.Migrate();

        using var connection = database.OpenConnection();
        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT name, \"notnull\" FROM pragma_table_info('projects') WHERE name = 'updated_at';";
        using var reader = columns.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("updated_at", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        using var migrations = connection.CreateCommand();
        migrations.CommandText = "SELECT version FROM schema_migrations;";
        Assert.Equal(2L, migrations.ExecuteScalar());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}