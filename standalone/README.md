# AI Doc standalone CLI

Use command-specific help to discover required parameters, allowed values, and examples:

```powershell
dotnet run --project src/AiDoc.Cli -- help project add --format json
dotnet run --project src/AiDoc.Cli -- project add --help --format yaml
dotnet run --project src/AiDoc.Cli -- help memory report --format json
```

```powershell
dotnet run --project src/AiDoc.Cli -- project add --name my-api --path D:\code\my-api --format json
dotnet run --project src/AiDoc.Cli -- project update my-api --new-name orders-api --format json
dotnet run --project src/AiDoc.Cli -- project list --name orders-api --name shared-lib --format json
dotnet run --project src/AiDoc.Cli -- memory report --project my-api --file memory.json --format json
dotnet run --project src/AiDoc.Cli -- memory import --project my-api --directory .\memories --format json
dotnet run --project src/AiDoc.Cli -- memory export <memory-id> --format json
dotnet run --project src/AiDoc.Cli -- embedding model download --format json
dotnet run --project src/AiDoc.Cli -- embedding backfill --project my-api --format json
dotnet run --project src/AiDoc.Cli -- search "login" --project my-api --format json
dotnet run --project src/AiDoc.Cli -- search "authentication" --project orders-api --project shared-lib --format json
dotnet run --project src/AiDoc.Cli -- search "deprecated" --all-projects --format json
dotnet run --project src/AiDoc.Cli -- project remove orders-api --yes --format json
dotnet run --project src/AiDoc.Cli -- search "login" --project my-api --format yaml
```

Set `AIDOC_HOME` to store the SQLite database and backups in a custom location. The default is `%LOCALAPPDATA%\AI Doc` on Windows. JSON writes use `--file` or `--stdin`; `stdout` contains final results and failures use a stable error document on `stderr`. Supported output formats are `text` (the default indented JSON), `json`, and readable `yaml` (or `yml`).