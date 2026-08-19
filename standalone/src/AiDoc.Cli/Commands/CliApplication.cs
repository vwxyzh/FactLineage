using System.Text.Json;
using System.Text.Json.Serialization;
using AiDoc.Cli.Application;
using AiDoc.Cli.Domain;
using AiDoc.Cli.Infrastructure;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiDoc.Cli.Commands;

public sealed class CliApplication
{
    private const int MaxInputBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .DisableAliases()
        .Build();

    public int Run(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        var format = OutputFormat.Text;
        try
        {
            format = GetOutputFormat(args);
            var home = GetHome();
            var database = new SqliteDatabase(Path.Combine(home, "aidoc.db"));
            database.Migrate();
            var projects = new ProjectService(database);
            var modelStore = new EmbeddingModelStore(Path.Combine(home, "models", "multilingual-e5-small"));
            using var embeddings = new OnnxEmbeddingProvider(modelStore.ModelDirectory);
            var memories = new MemoryService(database, projects, new GitInspector(), embeddings);
            var result = Execute(args, input, projects, memories, new MaintenanceService(database, projects, embeddings), modelStore);
            Write(output, new { schemaVersion = 1, data = result }, format);
            return 0;
        }
        catch (DomainException exception)
        {
            Write(error, new { schemaVersion = 1, error = new { code = exception.Code, message = exception.Message, details = new { } } }, format);
            return exception.Code.StartsWith("INVALID_", StringComparison.Ordinal) || exception.Code == "CODE_REFERENCE_OUTSIDE_PROJECT" ? 2 : 3;
        }
        catch (JsonException exception)
        {
            Write(error, new { schemaVersion = 1, error = new { code = "INVALID_INPUT_JSON", message = exception.Message, details = new { } } }, format);
            return 2;
        }
        catch (IOException exception)
        {
            Write(error, new { schemaVersion = 1, error = new { code = "EXTERNAL_DEPENDENCY_FAILURE", message = exception.Message, details = new { } } }, format);
            return 4;
        }
        catch (HttpRequestException exception)
        {
            Write(error, new { schemaVersion = 1, error = new { code = "EXTERNAL_DEPENDENCY_FAILURE", message = exception.Message, details = new { } } }, format);
            return 4;
        }
        catch (Exception exception)
        {
            Write(error, new { schemaVersion = 1, error = new { code = "INTERNAL_ERROR", message = exception.Message, details = new { } } }, format);
            return 5;
        }
    }

    private static object Execute(string[] args, TextReader input, ProjectService projects, MemoryService memories, MaintenanceService maintenance, EmbeddingModelStore modelStore)
    {
        if (IsHelpRequest(args)) return GetHelp(args);
        if (args is ["version", ..]) return new { version = "0.1.0", schemaVersion = 1 };
        if (args is ["project", "add", ..]) return projects.Add(new CreateProjectRequest(Required(args, "name"), Required(args, "path"), Option(args, "remote-url")));
        if (args is ["project", "list", ..]) { var names = Options(args, "name"); return names.Count == 0 ? projects.List() : projects.GetMany(names); }
        if (args is ["project", "show", _, ..]) return projects.Get(args[2]);
        if (args is ["project", "update", _, ..]) return projects.Update(args[2], new UpdateProjectRequest(Option(args, "new-name"), Option(args, "path"), Option(args, "remote-url"), args.Contains("--clear-remote-url")));
        if (args is ["project", "remove", _, ..])
        {
            if (!args.Contains("--yes")) throw new DomainException("CONFIRMATION_REQUIRED", "Project removal requires --yes.");
            var removal = projects.Remove(args[2]);
            return new { projectId = removal.Project.Id, projectName = removal.Project.Name, memoriesRemoved = removal.MemoriesRemoved, memoryVersionsRemoved = removal.MemoryVersionsRemoved, searchDocumentsRemoved = removal.SearchDocumentsRemoved };
        }
        if (args is ["memory", "report", ..]) { var request = ReadInput<ReportInput>(args, input); var version = memories.Report(Required(args, "project"), request.ToRequest(), args.Contains("--allow-missing-references")); return new { memoryId = version.MemoryId, version = version.Version, commitSha = version.CommitSha, embeddingStatus = version.EmbeddingModel is null ? "pending" : "complete" }; }
        if (args is ["memory", "import", ..]) return ImportDirectory(memories, Required(args, "project"), Required(args, "directory"), args.Contains("--allow-missing-references"));
        if (args is ["memory", "revise", var memoryId, ..]) { var request = ReadInput<RevisionInput>(args, input); var version = memories.Revise(memoryId, request.ToRequest(), args.Contains("--allow-missing-references")); return new { memoryId = version.MemoryId, version = version.Version, commitSha = version.CommitSha, embeddingStatus = version.EmbeddingModel is null ? "pending" : "complete" }; }
        if (args is ["memory", "get", _, ..]) { var (memory, version) = memories.Get(args[2]); return new { memory, version, codeReferences = JsonSerializer.Deserialize<JsonElement>(version.CodeReferencesJson, JsonOptions) }; }
        if (args is ["memory", "export", _, ..]) return ExportMemory(memories, args[2]);
        if (args is ["memory", "history", _, ..]) return memories.History(args[2]);
        if (args is ["search", var query, ..]) return memories.Search(Options(args, "project"), args.Contains("--all-projects"), query, Option(args, "type"), ParseLimit(Option(args, "limit")))
            .Select(result => new { projectId = result.Project.Id, projectName = result.Project.Name, memory = result.Memory, version = result.Version, score = result.Score });
        if (args is ["doctor", ..]) return maintenance.Doctor();
        if (args is ["backup", ..]) return new { backupPath = maintenance.Backup(Path.Combine(GetHome(), "backups")) };
        if (args is ["embedding", "model", "download", ..]) { modelStore.Download(); return new { model = "multilingual-e5-small:384", modelDirectory = modelStore.ModelDirectory, status = "ready" }; }
        if (args is ["embedding", "backfill", ..]) return maintenance.Backfill(Required(args, "project"));
        throw new DomainException("INVALID_COMMAND", "Unknown command. Run 'aidoc help' for usage.");
    }

    private static string GetHome() => Environment.GetEnvironmentVariable("AIDOC_HOME") is { Length: > 0 } home ? Path.GetFullPath(home) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Doc");
    private static OutputFormat GetOutputFormat(string[] args) => Option(args, "format")?.ToLowerInvariant() switch
    {
        null or "text" => OutputFormat.Text,
        "json" => OutputFormat.Json,
        "yaml" or "yml" => OutputFormat.Yaml,
        var value => throw new DomainException("INVALID_OUTPUT_FORMAT", $"Unsupported output format '{value}'. Use text, json, or yaml.")
    };
    private static string Required(string[] args, string option) => Option(args, option) ?? throw new DomainException("INVALID_ARGUMENT", $"--{option} is required.");
    private static string? Option(string[] args, string option) { var index = Array.IndexOf(args, $"--{option}"); return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null; }
    private static IReadOnlyList<string> Options(string[] args, string option) => args
        .Select((value, index) => new { value, index })
        .Where(item => item.value == $"--{option}" && item.index + 1 < args.Length && !args[item.index + 1].StartsWith("--", StringComparison.Ordinal))
        .Select(item => args[item.index + 1])
        .ToList();
    private static int ParseLimit(string? value) => value is null ? 10 : int.TryParse(value, out var limit) ? limit : throw new DomainException("INVALID_LIMIT", "--limit must be an integer.");
    private static T ReadInput<T>(string[] args, TextReader input)
    {
        var file = Option(args, "file"); var fromStdin = args.Contains("--stdin");
        if ((file is null) == !fromStdin) throw new DomainException("INVALID_INPUT_SOURCE", "Specify exactly one of --file or --stdin.");
        var content = file is not null ? File.ReadAllText(file) : input.ReadToEnd();
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxInputBytes) throw new DomainException("INPUT_TOO_LARGE", "Input JSON exceeds 1 MB.");
        return JsonSerializer.Deserialize<T>(content, JsonOptions) ?? throw new DomainException("INVALID_INPUT_JSON", "Input JSON is required.");
    }
    private static object ImportDirectory(MemoryService memories, string projectName, string directory, bool allowMissingReferences)
    {
        if (!Directory.Exists(directory))
        {
            throw new DomainException("INVALID_IMPORT_DIRECTORY", "--directory must be an existing directory.");
        }

        var root = Path.GetFullPath(directory);
        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var imported = new List<object>();
        var failures = new List<object>();
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(root, file);
            try
            {
                var content = File.ReadAllText(file);
                if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxInputBytes)
                {
                    throw new DomainException("INPUT_TOO_LARGE", "Input JSON exceeds 1 MB.");
                }

                var request = JsonSerializer.Deserialize<ReportInput>(content, JsonOptions) ?? throw new DomainException("INVALID_INPUT_JSON", "Input JSON is required.");
                var version = memories.Report(projectName, request.ToRequest(), allowMissingReferences);
                imported.Add(new { path = relativePath, memoryId = version.MemoryId, version = version.Version, embeddingStatus = version.EmbeddingModel is null ? "pending" : "complete" });
            }
            catch (DomainException exception)
            {
                failures.Add(new { path = relativePath, code = exception.Code, message = exception.Message });
            }
            catch (JsonException exception)
            {
                failures.Add(new { path = relativePath, code = "INVALID_INPUT_JSON", message = exception.Message });
            }
            catch (IOException exception)
            {
                failures.Add(new { path = relativePath, code = "EXTERNAL_DEPENDENCY_FAILURE", message = exception.Message });
            }
        }

        return new { scanned = files.Count, imported = imported.Count, failed = failures.Count, imports = imported, failures };
    }
    private static object ExportMemory(MemoryService memories, string memoryId)
    {
        var (memory, version) = memories.Get(memoryId);
        return new
        {
            memoryId = memory.Id,
            version = version.Version,
            document = new
            {
                type = memory.Type,
                title = memory.Title,
                summary = version.Summary,
                details = JsonSerializer.Deserialize<JsonElement>(version.DetailsJson, JsonOptions),
                codeReferences = JsonSerializer.Deserialize<JsonElement>(version.CodeReferencesJson, JsonOptions),
                createdBy = version.CreatedBy
            }
        };
    }
    private static void Write(TextWriter writer, object value, OutputFormat format)
    {
        var content = format == OutputFormat.Yaml
            ? YamlSerializer.Serialize(value)
            : JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonOptions) { WriteIndented = format == OutputFormat.Text });
        writer.WriteLine(content);
    }
    private static bool IsHelpRequest(string[] args) => args.Length == 0 || args[0] is "--help" or "help" || args.Contains("--help");

    private static object GetHelp(string[] args)
    {
        var command = args
            .TakeWhile(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            .Where(argument => argument != "help")
            .Take(3)
            .ToArray();
        var name = string.Join(' ', command);
        return name switch
        {
            "" => new
            {
                name = "aidoc",
                description = "Store and retrieve project-scoped engineering memory.",
                usage = "aidoc <command> [options]",
                commands = new[]
                {
                    new { name = "project", description = "Create, modify, query, and remove registered projects." },
                    new { name = "memory", description = "Create, revise, and retrieve memory records." },
                    new { name = "search", description = "Search memory in explicitly selected project scopes." },
                    new { name = "embedding", description = "Download the local model and backfill memory embeddings." },
                    new { name = "doctor", description = "Check database and registered project health." },
                    new { name = "backup", description = "Create a SQLite database backup." },
                    new { name = "version", description = "Show CLI and output schema versions." }
                },
                examples = new[] { "aidoc help project add", "aidoc search \"login\" --project my-api --format json" }
            },
            "project" => new
            {
                name = "project",
                description = "Manage locally registered projects. Run 'aidoc help project <subcommand>' for details.",
                commands = new[]
                {
                    new { name = "add", description = "Register a project." },
                    new { name = "update", description = "Modify one project." },
                    new { name = "list", description = "List selected or all projects." },
                    new { name = "show", description = "Show one project." },
                    new { name = "remove", description = "Remove one project and its memories." }
                }
            },
            "project add" => CommandHelp("project add", "Register a local project directory.", "aidoc project add --name <name> --path <path> [--remote-url <url>] [--format <format>]", [], [
                new HelpOption("--name <name>", true, "Unique local project name."),
                new HelpOption("--path <path>", true, "Existing project directory; stored as an absolute path."),
                new HelpOption("--remote-url <url>", false, "Optional Git remote URL."),
                FormatOption
            ], ["aidoc project add --name my-api --path D:\\code\\my-api --format json"]),
            "project update" => CommandHelp("project update", "Modify a registered project without changing its project ID.", "aidoc project update <name> [--new-name <name>] [--path <path>] [--remote-url <url> | --clear-remote-url] [--format <format>]", [new HelpArgument("<name>", "Existing project name.")], [
                new HelpOption("--new-name <name>", false, "Replacement unique project name."),
                new HelpOption("--path <path>", false, "Replacement existing project directory."),
                new HelpOption("--remote-url <url>", false, "Set the Git remote URL."),
                new HelpOption("--clear-remote-url", false, "Clear the Git remote URL; mutually exclusive with --remote-url."),
                FormatOption
            ], ["aidoc project update my-api --new-name orders-api"]),
            "project list" => CommandHelp("project list", "List all projects or selected projects in the requested order.", "aidoc project list [--name <name> ...] [--format <format>]", [], [new HelpOption("--name <name>", false, "Repeat to select one or more projects; omit for all projects."), FormatOption], ["aidoc project list --name orders-api --name shared-lib --format json"]),
            "project show" => CommandHelp("project show", "Show one registered project.", "aidoc project show <name> [--format <format>]", [new HelpArgument("<name>", "Existing project name.")], [FormatOption], ["aidoc project show my-api --format json"]),
            "project remove" => CommandHelp("project remove", "Remove a project and all of its memory records without deleting source files.", "aidoc project remove <name> --yes [--format <format>]", [new HelpArgument("<name>", "Existing project name.")], [new HelpOption("--yes", true, "Required confirmation for this destructive operation."), FormatOption], ["aidoc project remove my-api --yes --format json"]),
            "memory" => new { name = "memory", description = "Manage immutable memory records. Run 'aidoc help memory <subcommand>' for details.", commands = new[] { "report", "import", "revise", "get", "export", "history" } },
            "memory report" => CommandHelp("memory report", "Create a memory and version 1 from JSON input.", "aidoc memory report --project <name> (--file <path> | --stdin) [--allow-missing-references] [--format <format>]", [], [new HelpOption("--project <name>", true, "Single target project."), new HelpOption("--file <path>", false, "JSON input file; mutually exclusive with --stdin."), new HelpOption("--stdin", false, "Read JSON input from standard input; mutually exclusive with --file."), new HelpOption("--allow-missing-references", false, "Allow referenced files that do not exist."), FormatOption], ["aidoc memory report --project my-api --file memory.json --format json"], ReportInputSchema),
            "memory import" => CommandHelp("memory import", "Recursively import JSON memory-report files from a directory. Invalid files are reported while other files continue importing.", "aidoc memory import --project <name> --directory <path> [--allow-missing-references] [--format <format>]", [], [new HelpOption("--project <name>", true, "Single target project."), new HelpOption("--directory <path>", true, "Existing directory recursively scanned for .json files."), new HelpOption("--allow-missing-references", false, "Allow referenced files that do not exist."), FormatOption], ["aidoc memory import --project my-api --directory .\\memories --format json"], ReportInputSchema),
            "memory revise" => CommandHelp("memory revise", "Append a new immutable version to an existing memory.", "aidoc memory revise <memory-id> (--file <path> | --stdin) [--allow-missing-references] [--format <format>]", [new HelpArgument("<memory-id>", "Existing memory ID.")], [new HelpOption("--file <path>", false, "JSON input file; mutually exclusive with --stdin."), new HelpOption("--stdin", false, "Read JSON input from standard input; mutually exclusive with --file."), new HelpOption("--allow-missing-references", false, "Allow referenced files that do not exist."), FormatOption], ["aidoc memory revise <memory-id> --file revision.json"], RevisionInputSchema),
            "memory get" => CommandHelp("memory get", "Get a memory and its current version.", "aidoc memory get <memory-id> [--format <format>]", [new HelpArgument("<memory-id>", "Existing memory ID.")], [FormatOption], ["aidoc memory get <memory-id> --format json"]),
            "memory export" => CommandHelp("memory export", "Export a memory's current version as a reusable memory report document.", "aidoc memory export <memory-id> [--format <format>]", [new HelpArgument("<memory-id>", "Existing memory ID.")], [FormatOption], ["aidoc memory export <memory-id> --format json"]),
            "memory history" => CommandHelp("memory history", "List all immutable versions of a memory.", "aidoc memory history <memory-id> [--format <format>]", [new HelpArgument("<memory-id>", "Existing memory ID.")], [FormatOption], ["aidoc memory history <memory-id> --format json"]),
            "search" => CommandHelp("search", "Search memory with hybrid keyword and local semantic retrieval.", "aidoc search <query> (--project <name> ... | --all-projects) [--type <type>] [--limit <count>] [--format <format>]", [new HelpArgument("<query>", "Natural-language or exact code-term query.")], [new HelpOption("--project <name>", false, "Repeat to select one or more projects; mutually exclusive with --all-projects."), new HelpOption("--all-projects", false, "Search every project visible when the command begins; mutually exclusive with --project."), new HelpOption("--type <type>", false, "Filter by feature, api, or decision."), new HelpOption("--limit <count>", false, "Global result limit from 1 through 100; defaults to 10."), FormatOption], ["aidoc search \"login\" --project my-api --format json", "aidoc search \"deprecated\" --all-projects"]),
            "embedding" => new { name = "embedding", description = "Manage the local multilingual-e5-small embedding model.", commands = new[] { "model download", "backfill" } },
            "embedding model download" => CommandHelp("embedding model download", "Download the local ONNX model and SentencePiece tokenizer.", "aidoc embedding model download [--format <format>]", [], [FormatOption], ["aidoc embedding model download --format json"]),
            "embedding backfill" => CommandHelp("embedding backfill", "Generate missing or outdated Embeddings for every memory version in one project.", "aidoc embedding backfill --project <name> [--format <format>]", [], [new HelpOption("--project <name>", true, "Project whose memory versions will be embedded."), FormatOption], ["aidoc embedding backfill --project my-api --format json"]),
            "doctor" => CommandHelp("doctor", "Check database integrity and registered project paths.", "aidoc doctor [--format <format>]", [], [FormatOption], ["aidoc doctor --format json"]),
            "backup" => CommandHelp("backup", "Create a SQLite database backup in the configured data directory.", "aidoc backup [--format <format>]", [], [FormatOption], ["aidoc backup --format json"]),
            "version" => CommandHelp("version", "Show the CLI and output schema versions.", "aidoc version [--format <format>]", [], [FormatOption], ["aidoc version --format json"]),
            _ => throw new DomainException("INVALID_COMMAND", $"Unknown command '{name}'. Run 'aidoc help' for usage.")
        };
    }

        private static object CommandHelp(string name, string description, string usage, IReadOnlyList<HelpArgument> arguments, IReadOnlyList<HelpOption> options, IReadOnlyList<string> examples, JsonElement? inputSchema = null) => new { name, description, usage, arguments, options, examples, inputSchema };

    private static readonly HelpOption FormatOption = new("--format <format>", false, "Output format: text (default), json, yaml, or yml.");
        private static readonly JsonElement ReportInputSchema = ParseSchema("""
                {
                    "$schema": "https://json-schema.org/draft/2020-12/schema",
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["type", "title", "summary", "createdBy"],
                    "properties": {
                        "type": { "type": "string", "enum": ["feature", "api", "decision"] },
                        "title": { "type": "string", "minLength": 1 },
                        "summary": { "type": "string", "minLength": 1 },
                        "details": {},
                        "codeReferences": { "type": "array", "items": { "$ref": "#/$defs/codeReference" } },
                        "createdBy": { "type": "string", "minLength": 1 }
                    },
                    "$defs": {
                        "codeReference": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["path", "startLine", "endLine"],
                            "properties": {
                                "path": { "type": "string", "minLength": 1 },
                                "symbol": { "type": ["string", "null"] },
                                "startLine": { "type": "integer", "minimum": 1 },
                                "endLine": { "type": "integer", "minimum": 1 }
                            }
                        }
                    }
                }
                """);
        private static readonly JsonElement RevisionInputSchema = ParseSchema("""
                {
                    "$schema": "https://json-schema.org/draft/2020-12/schema",
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["summary", "createdBy"],
                    "properties": {
                        "summary": { "type": "string", "minLength": 1 },
                        "details": {},
                        "codeReferences": { "type": "array", "items": { "$ref": "#/$defs/codeReference" } },
                        "createdBy": { "type": "string", "minLength": 1 }
                    },
                    "$defs": {
                        "codeReference": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["path", "startLine", "endLine"],
                            "properties": {
                                "path": { "type": "string", "minLength": 1 },
                                "symbol": { "type": ["string", "null"] },
                                "startLine": { "type": "integer", "minimum": 1 },
                                "endLine": { "type": "integer", "minimum": 1 }
                            }
                        }
                    }
                }
                """);
        private static JsonElement ParseSchema(string schema) => JsonDocument.Parse(schema).RootElement.Clone();

    private enum OutputFormat { Text, Json, Yaml }
    private sealed record HelpArgument(string Name, string Description);
    private sealed record HelpOption(string Name, bool Required, string Description);
    private sealed record ReportInput(string Type, string Title, string Summary, JsonElement? Details, IReadOnlyList<CodeReference>? CodeReferences, string CreatedBy) { public MemoryReportRequest ToRequest() => new(Type, Title, Summary, Details, CodeReferences ?? [], CreatedBy); }
    private sealed record RevisionInput(string Summary, JsonElement? Details, IReadOnlyList<CodeReference>? CodeReferences, string CreatedBy) { public MemoryRevisionRequest ToRequest() => new(Summary, Details, CodeReferences ?? [], CreatedBy); }
}