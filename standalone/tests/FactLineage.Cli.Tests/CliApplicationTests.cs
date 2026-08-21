using System.Text.Json;
using FactLineage.Cli.Commands;

namespace FactLineage.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "factlineage-cli-tests", Guid.NewGuid().ToString());

    [Fact]
    public void Run_ReportsAndSearchesMemoryWithJsonContracts()
    {
        Directory.CreateDirectory(_root);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(_root, "project"));
        File.WriteAllText(Path.Combine(projectDirectory.FullName, "Login.cs"), "public class Login { }");
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", Path.Combine(_root, "home"));
        var app = new CliApplication();
        var add = Run(app, ["project", "add", "--name", "sample", "--path", projectDirectory.FullName, "--format", "json"]);
        var report = Run(app, ["memory", "report", "--project", "sample", "--stdin", "--format", "json"], """{"type":"feature","title":"Login","summary":"Handles login requests.","details":{"route":"/login"},"codeReferences":[{"path":"Login.cs","symbol":"Login","startLine":1,"endLine":1}],"createdBy":"test"}""");
        var search = Run(app, ["search", "login", "--project", "sample", "--format", "json"]);
        Assert.Equal(0, add.ExitCode); Assert.Equal(0, report.ExitCode); Assert.Equal(0, search.ExitCode);
        Assert.Equal(1, JsonDocument.Parse(search.Output).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Single(JsonDocument.Parse(search.Output).RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public void Run_UsesStableErrorForMissingProject()
    {
        Directory.CreateDirectory(_root); Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", Path.Combine(_root, "home"));
        var result = Run(new CliApplication(), ["search", "login", "--project", "missing", "--format", "json"]);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("PROJECT_NOT_FOUND", JsonDocument.Parse(result.Error).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Run_SupportsProjectUpdateAndExplicitMultiProjectSearch()
    {
        Directory.CreateDirectory(_root);
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_root, "first"));
        var secondDirectory = Directory.CreateDirectory(Path.Combine(_root, "second"));
        File.WriteAllText(Path.Combine(firstDirectory.FullName, "First.cs"), "public class First { }");
        File.WriteAllText(Path.Combine(secondDirectory.FullName, "Second.cs"), "public class Second { }");
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", Path.Combine(_root, "home"));
        var app = new CliApplication();
        Assert.Equal(0, Run(app, ["project", "add", "--name", "first", "--path", firstDirectory.FullName]).ExitCode);
        Assert.Equal(0, Run(app, ["project", "add", "--name", "second", "--path", secondDirectory.FullName]).ExitCode);
        Assert.Equal(0, Run(app, ["project", "update", "first", "--new-name", "renamed"]).ExitCode);
        var singleProject = Run(app, ["project", "show", "renamed", "--format", "json"]);
        var selectedProjects = Run(app, ["project", "list", "--name", "second", "--name", "renamed", "--format", "json"]);
        var allProjects = Run(app, ["project", "list", "--format", "json"]);
        Assert.Equal(0, Run(app, ["memory", "report", "--project", "renamed", "--stdin"], """{"type":"feature","title":"First login","summary":"First login.","codeReferences":[{"path":"First.cs","startLine":1,"endLine":1}],"createdBy":"test"}""").ExitCode);
        Assert.Equal(0, Run(app, ["memory", "report", "--project", "second", "--stdin"], """{"type":"feature","title":"Second login","summary":"Second login.","codeReferences":[{"path":"Second.cs","startLine":1,"endLine":1}],"createdBy":"test"}""").ExitCode);

        var search = Run(app, ["search", "login", "--project", "renamed", "--project", "second", "--format", "json"]);
        var missingScope = Run(app, ["search", "login", "--format", "json"]);
        var mixedScope = Run(app, ["search", "login", "--project", "renamed", "--all-projects", "--format", "json"]);

        Assert.Equal("renamed", JsonDocument.Parse(singleProject.Output).RootElement.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal(2, JsonDocument.Parse(selectedProjects.Output).RootElement.GetProperty("data").GetArrayLength());
        Assert.Equal(2, JsonDocument.Parse(allProjects.Output).RootElement.GetProperty("data").GetArrayLength());
        Assert.Equal(0, search.ExitCode);
        Assert.Equal(2, JsonDocument.Parse(search.Output).RootElement.GetProperty("data").GetArrayLength());
        Assert.True(JsonDocument.Parse(search.Output).RootElement.GetProperty("data")[0].TryGetProperty("projectName", out _));
        Assert.Equal("PROJECT_SCOPE_REQUIRED", JsonDocument.Parse(missingScope.Error).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("PROJECT_SCOPE_REQUIRED", JsonDocument.Parse(mixedScope.Error).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Run_SearchesAllProjectsAndRemovesOneProjectWithItsMemories()
    {
        Directory.CreateDirectory(_root);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(_root, "project"));
        File.WriteAllText(Path.Combine(projectDirectory.FullName, "Login.cs"), "public class Login { }");
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", Path.Combine(_root, "home"));
        var app = new CliApplication();
        Assert.Equal(0, Run(app, ["project", "add", "--name", "sample", "--path", projectDirectory.FullName]).ExitCode);
        Assert.Equal(0, Run(app, ["memory", "report", "--project", "sample", "--stdin"], """{"type":"feature","title":"Login","summary":"Login behavior.","codeReferences":[{"path":"Login.cs","startLine":1,"endLine":1}],"createdBy":"test"}""").ExitCode);

        var allProjects = Run(app, ["search", "login", "--all-projects", "--format", "json"]);
        var removal = Run(app, ["project", "remove", "sample", "--yes", "--format", "json"]);
        var afterRemoval = Run(app, ["search", "login", "--all-projects", "--format", "json"]);

        Assert.Equal(1, JsonDocument.Parse(allProjects.Output).RootElement.GetProperty("data").GetArrayLength());
        Assert.Equal(1, JsonDocument.Parse(removal.Output).RootElement.GetProperty("data").GetProperty("memoriesRemoved").GetInt32());
        Assert.Equal(0, JsonDocument.Parse(afterRemoval.Output).RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public void Run_WritesReadableYamlForSuccessAndErrors()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", Path.Combine(_root, "home"));
        var app = new CliApplication();

        var success = Run(app, ["version", "--format", "yaml"]);
        var failure = Run(app, ["search", "login", "--project", "missing", "--format", "yaml"]);

        Assert.Equal(0, success.ExitCode);
        Assert.Contains("schemaVersion: 1", success.Output);
        Assert.Contains("data:", success.Output);
        Assert.Contains("version: 0.1.0", success.Output);
        Assert.Equal(3, failure.ExitCode);
        Assert.Contains("error:", failure.Error);
        Assert.Contains("code: PROJECT_NOT_FOUND", failure.Error);
    }

    [Fact]
    public void Run_RejectsUnsupportedOutputFormat()
    {
        var result = Run(new CliApplication(), ["version", "--format", "xml"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("INVALID_OUTPUT_FORMAT", JsonDocument.Parse(result.Error).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Run_ReturnsDetailedHelpForProjectAdd()
    {
        var app = new CliApplication();

        var directHelp = Run(app, ["project", "add", "--help", "--format", "json"]);
        var helpCommand = Run(app, ["help", "project", "add", "--format", "json"]);
        var rootHelp = Run(app, ["--help", "--format", "json"]);

        Assert.Equal(0, directHelp.ExitCode);
        Assert.Equal(0, helpCommand.ExitCode);
        Assert.Equal(0, rootHelp.ExitCode);
        var data = JsonDocument.Parse(directHelp.Output).RootElement.GetProperty("data");
        Assert.Equal("project add", data.GetProperty("name").GetString());
        Assert.Contains("--name <name>", data.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("name").GetString()));
        Assert.Contains("--path <path>", data.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("name").GetString()));
        Assert.Equal(JsonDocument.Parse(directHelp.Output).RootElement.GetProperty("data").GetProperty("usage").GetString(), JsonDocument.Parse(helpCommand.Output).RootElement.GetProperty("data").GetProperty("usage").GetString());
        Assert.Equal("factlineage", JsonDocument.Parse(rootHelp.Output).RootElement.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public void Run_ReturnsJsonInputSchemasForMemoryWriteCommands()
    {
        var app = new CliApplication();

        var reportHelp = Run(app, ["help", "memory", "report", "--format", "json"]);
        var revisionHelp = Run(app, ["memory", "revise", "--help", "--format", "json"]);

        Assert.Equal(0, reportHelp.ExitCode);
        Assert.Equal(0, revisionHelp.ExitCode);
        var reportSchema = JsonDocument.Parse(reportHelp.Output).RootElement.GetProperty("data").GetProperty("inputSchema");
        var revisionSchema = JsonDocument.Parse(revisionHelp.Output).RootElement.GetProperty("data").GetProperty("inputSchema");
        Assert.Contains("type", reportSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("title", reportSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("feature", reportSchema.GetProperty("properties").GetProperty("type").GetProperty("enum")[0].GetString());
        Assert.Contains("summary", revisionSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.False(revisionSchema.GetProperty("properties").TryGetProperty("title", out _));
    }

    [Fact]
    public void Run_ReturnsHelpForLocalEmbeddingCommands()
    {
        var modelHelp = Run(new CliApplication(), ["help", "embedding", "model", "download", "--format", "json"]);
        var backfillHelp = Run(new CliApplication(), ["embedding", "backfill", "--help", "--format", "json"]);

        Assert.Equal("embedding model download", JsonDocument.Parse(modelHelp.Output).RootElement.GetProperty("data").GetProperty("name").GetString());
        var options = JsonDocument.Parse(backfillHelp.Output).RootElement.GetProperty("data").GetProperty("options").EnumerateArray();
        Assert.Contains("--project <name>", options.Select(option => option.GetProperty("name").GetString()));
    }

    [Fact]
    public void Run_ImportsJsonReportsRecursivelyAndReturnsPerFileFailures()
    {
        Directory.CreateDirectory(_root);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(_root, "project"));
        File.WriteAllText(Path.Combine(projectDirectory.FullName, "Login.cs"), "public class Login { }");
        var importDirectory = Directory.CreateDirectory(Path.Combine(_root, "imports"));
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(importDirectory.FullName, "nested"));
        File.WriteAllText(Path.Combine(importDirectory.FullName, "first.json"), """{"type":"feature","title":"Login","summary":"First login report.","codeReferences":[{"path":"Login.cs","startLine":1,"endLine":1}],"createdBy":"test"}""");
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "second.json"), """{"type":"api","title":"Session","summary":"Creates a session.","codeReferences":[{"path":"Login.cs","startLine":1,"endLine":1}],"createdBy":"test"}""");
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "invalid.json"), "{ invalid json }");
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", Path.Combine(_root, "home"));
        var app = new CliApplication();
        Assert.Equal(0, Run(app, ["project", "add", "--name", "sample", "--path", projectDirectory.FullName]).ExitCode);

        var import = Run(app, ["memory", "import", "--project", "sample", "--directory", importDirectory.FullName, "--format", "json"]);
        var search = Run(app, ["search", "login", "--project", "sample", "--format", "json"]);

        Assert.Equal(0, import.ExitCode);
        var data = JsonDocument.Parse(import.Output).RootElement.GetProperty("data");
        Assert.Equal(3, data.GetProperty("scanned").GetInt32());
        Assert.Equal(2, data.GetProperty("imported").GetInt32());
        Assert.Equal(1, data.GetProperty("failed").GetInt32());
        Assert.Equal("nested\\invalid.json", data.GetProperty("failures")[0].GetProperty("path").GetString());
        Assert.Equal("INVALID_INPUT_JSON", data.GetProperty("failures")[0].GetProperty("code").GetString());
        Assert.Equal(2, JsonDocument.Parse(search.Output).RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public void Run_ExportsCurrentMemoryDocumentById()
    {
        Directory.CreateDirectory(_root);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(_root, "project"));
        File.WriteAllText(Path.Combine(projectDirectory.FullName, "Login.cs"), "public class Login { }");
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", Path.Combine(_root, "home"));
        var app = new CliApplication();
        Assert.Equal(0, Run(app, ["project", "add", "--name", "sample", "--path", projectDirectory.FullName]).ExitCode);
        var report = Run(app, ["memory", "report", "--project", "sample", "--stdin", "--format", "json"], """{"type":"feature","title":"Login","summary":"Handles login.","details":{"route":"/login"},"codeReferences":[{"path":"Login.cs","symbol":"Login","startLine":1,"endLine":1}],"createdBy":"test"}""");
        var memoryId = JsonDocument.Parse(report.Output).RootElement.GetProperty("data").GetProperty("memoryId").GetString()!;

        var export = Run(app, ["memory", "export", memoryId, "--format", "json"]);

        Assert.Equal(0, export.ExitCode);
        var data = JsonDocument.Parse(export.Output).RootElement.GetProperty("data");
        Assert.Equal(memoryId, data.GetProperty("memoryId").GetString());
        var document = data.GetProperty("document");
        Assert.Equal("feature", document.GetProperty("type").GetString());
        Assert.Equal("Login", document.GetProperty("title").GetString());
        Assert.Equal("/login", document.GetProperty("details").GetProperty("route").GetString());
        Assert.Equal("Login.cs", document.GetProperty("codeReferences")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Run_PrefersFactLineageHomeOverLegacyHome()
    {
        Directory.CreateDirectory(_root);
        var home = Path.Combine(_root, "current-home");
        var legacyHome = Path.Combine(_root, "legacy-home");
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", home);
        Environment.SetEnvironmentVariable("AIDOC_HOME", legacyHome);

        var result = Run(new CliApplication(), ["version", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(home, "factlineage.db")));
        Assert.False(File.Exists(Path.Combine(legacyHome, "aidoc.db")));
    }

    [Fact]
    public void Run_ReusesLegacyDatabaseFromAidocHome()
    {
        Directory.CreateDirectory(_root);
        var home = Path.Combine(_root, "legacy-home");
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", null);
        Environment.SetEnvironmentVariable("AIDOC_HOME", home);
        Assert.Equal(0, Run(new CliApplication(), ["version"]).ExitCode);
        Assert.True(File.Exists(Path.Combine(home, "aidoc.db")));

        Environment.SetEnvironmentVariable("AIDOC_HOME", null);
        Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", home);
        Assert.Equal(0, Run(new CliApplication(), ["version"]).ExitCode);

        Assert.True(File.Exists(Path.Combine(home, "aidoc.db")));
        Assert.False(File.Exists(Path.Combine(home, "factlineage.db")));
    }

    private static (int ExitCode, string Output, string Error) Run(CliApplication app, string[] args, string input = "") { using var output = new StringWriter(); using var error = new StringWriter(); return (app.Run(args, new StringReader(input), output, error), output.ToString(), error.ToString()); }
    public void Dispose() { Environment.SetEnvironmentVariable("FACTLINEAGE_HOME", null); Environment.SetEnvironmentVariable("AIDOC_HOME", null); if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}