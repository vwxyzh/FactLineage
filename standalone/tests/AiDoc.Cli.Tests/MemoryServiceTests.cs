using AiDoc.Cli.Application;
using AiDoc.Cli.Domain;
using AiDoc.Cli.Infrastructure;

namespace AiDoc.Cli.Tests;

public sealed class MemoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aidoc-tests", Guid.NewGuid().ToString());

    [Fact]
    public void ReportReviseHistoryAndSearch_KeepImmutableVersionsAndProjectScope()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "aidoc.db"));
        database.Migrate();
        var projects = new ProjectService(database);
        var firstRoot = CreateProjectDirectory("first", "LoginService.cs");
        var secondRoot = CreateProjectDirectory("second", "OtherService.cs");
        projects.Add(new CreateProjectRequest("first", firstRoot));
        projects.Add(new CreateProjectRequest("second", secondRoot));
        var service = new MemoryService(database, projects, new GitInspector());

        var versionOne = service.Report("first", new MemoryReportRequest("api", "Login endpoint", "Validates credentials for POST login.", new { endpoint = "POST /login" }, [new CodeReference("LoginService.cs", "LoginService.LoginAsync", 1, 2)], "test"));
        var versionTwo = service.Revise(versionOne.MemoryId, new MemoryRevisionRequest("Issues an access token after credential validation.", new { endpoint = "POST /login" }, [new CodeReference("LoginService.cs", "LoginService.LoginAsync", 1, 2)], "test"));
        service.Report("second", new MemoryReportRequest("api", "Other login", "Login behavior for another project.", null, [new CodeReference("OtherService.cs", null, 1, 1)], "test"));

        var history = service.History(versionOne.MemoryId);
        var search = service.Search(["first"], false, "login");

        Assert.Equal(2, history.Count);
        Assert.Equal("Validates credentials for POST login.", history[0].Summary);
        Assert.Equal(2, versionTwo.Version);
        var result = Assert.Single(search);
        Assert.Equal(versionOne.MemoryId, result.Memory.Id);
        Assert.Equal("first", result.Project.Name);
        Assert.Equal(2, result.Version.Version);
    }

    [Fact]
    public void Search_ReturnsMatchesAcrossSelectedProjects()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "aidoc.db"));
        database.Migrate();
        var projects = new ProjectService(database);
        projects.Add(new CreateProjectRequest("first", CreateProjectDirectory("first", "First.cs")));
        projects.Add(new CreateProjectRequest("second", CreateProjectDirectory("second", "Second.cs")));
        var service = new MemoryService(database, projects, new GitInspector());
        service.Report("first", new MemoryReportRequest("feature", "First login", "First login flow.", null, [new CodeReference("First.cs", null, 1, 1)], "test"));
        service.Report("second", new MemoryReportRequest("feature", "Second login", "Second login flow.", null, [new CodeReference("Second.cs", null, 1, 1)], "test"));

        var results = service.Search(["first", "second"], false, "login");

        Assert.Equal(["first", "second"], results.Select(result => result.Project.Name).Order());
    }

    [Fact]
    public void Search_ReturnsSemanticMatchesWithoutKeywordOverlap()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "aidoc.db"));
        database.Migrate();
        var projects = new ProjectService(database);
        projects.Add(new CreateProjectRequest("first", CreateProjectDirectory("first", "Login.cs")));
        var service = new MemoryService(database, projects, new GitInspector(), new TestEmbeddingProvider());
        service.Report("first", new MemoryReportRequest("feature", "Login flow", "Issues an access token.", null, [new CodeReference("Login.cs", null, 1, 1)], "test"));

        var result = Assert.Single(service.Search(["first"], false, "authentication"));

        Assert.Equal("Login flow", result.Memory.Title);
        Assert.Equal("test:2", result.Version.EmbeddingModel);
    }

    [Fact]
    public void Backfill_EmbedsPendingVersionsForSemanticSearch()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "aidoc.db"));
        database.Migrate();
        var projects = new ProjectService(database);
        projects.Add(new CreateProjectRequest("first", CreateProjectDirectory("first", "Login.cs")));
        var writer = new MemoryService(database, projects, new GitInspector());
        var version = writer.Report("first", new MemoryReportRequest("feature", "Login flow", "Issues an access token.", null, [new CodeReference("Login.cs", null, 1, 1)], "test"));
        var maintenance = new MaintenanceService(database, projects, new TestEmbeddingProvider());

        var backfill = maintenance.Backfill("first");
        var result = Assert.Single(new MemoryService(database, projects, new GitInspector(), new TestEmbeddingProvider()).Search(["first"], false, "authentication"));

        Assert.Equal(1, backfill.Scanned);
        Assert.Equal(1, backfill.Updated);
        Assert.Equal(version.MemoryId, result.Memory.Id);
    }

    [Fact]
    public void Report_FallsBackToKeywordSearchWhenEmbeddingFails()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "aidoc.db"));
        database.Migrate();
        var projects = new ProjectService(database);
        projects.Add(new CreateProjectRequest("first", CreateProjectDirectory("first", "Login.cs")));
        var service = new MemoryService(database, projects, new GitInspector(), new ThrowingEmbeddingProvider());

        var version = service.Report("first", new MemoryReportRequest("feature", "Login flow", "Login with password.", null, [new CodeReference("Login.cs", null, 1, 1)], "test"));
        var result = Assert.Single(service.Search(["first"], false, "login"));

        Assert.Null(version.EmbeddingModel);
        Assert.Equal(version.MemoryId, result.Memory.Id);
    }

    [Fact]
    public void Report_RejectsReferenceOutsideProject()
    {
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "aidoc.db")); database.Migrate();
        var projects = new ProjectService(database);
        projects.Add(new CreateProjectRequest("first", CreateProjectDirectory("first", "Inside.cs")));
        var service = new MemoryService(database, projects, new GitInspector());

        var exception = Assert.Throws<DomainException>(() => service.Report("first", new MemoryReportRequest("feature", "Outside", "Outside path.", null, [new CodeReference("../outside.cs", null, 1, 1)], "test"), true));

        Assert.Equal("CODE_REFERENCE_OUTSIDE_PROJECT", exception.Code);
    }

    private string CreateProjectDirectory(string name, string fileName)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, name));
        File.WriteAllText(Path.Combine(directory.FullName, fileName), "public class Example { }");
        return directory.FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class TestEmbeddingProvider : IEmbeddingProvider
    {
        public string Model => "test:2";

        public bool IsAvailable => true;

        public Embedding? Create(string text, EmbeddingKind kind) =>
            text.Contains("authentication", StringComparison.OrdinalIgnoreCase) || text.Contains("Login", StringComparison.OrdinalIgnoreCase)
                ? new Embedding(Model, [1, 0])
                : new Embedding(Model, [0, 1]);
    }

    private sealed class ThrowingEmbeddingProvider : IEmbeddingProvider
    {
        public string Model => "test:broken";

        public bool IsAvailable => true;

        public Embedding? Create(string text, EmbeddingKind kind) => throw new InvalidOperationException("Inference failed.");
    }
}