# FactLineage standalone CLI

Use command-specific help to discover required parameters, allowed values, and examples:

```powershell
dotnet run --project src/FactLineage.Cli -- help project add --format json
dotnet run --project src/FactLineage.Cli -- project add --help --format yaml
dotnet run --project src/FactLineage.Cli -- help memory report --format json
```

```powershell
dotnet run --project src/FactLineage.Cli -- project add --name my-api --path D:\code\my-api --format json
dotnet run --project src/FactLineage.Cli -- project update my-api --new-name orders-api --format json
dotnet run --project src/FactLineage.Cli -- project list --name orders-api --name shared-lib --format json
dotnet run --project src/FactLineage.Cli -- memory report --project my-api --file memory.json --format json
dotnet run --project src/FactLineage.Cli -- memory import --project my-api --directory .\memories --format json
dotnet run --project src/FactLineage.Cli -- memory export <memory-id> --format json
dotnet run --project src/FactLineage.Cli -- embedding model download --format json
dotnet run --project src/FactLineage.Cli -- embedding backfill --project my-api --format json
dotnet run --project src/FactLineage.Cli -- search "login" --project my-api --format json
dotnet run --project src/FactLineage.Cli -- search "authentication" --project orders-api --project shared-lib --format json
dotnet run --project src/FactLineage.Cli -- search "deprecated" --all-projects --format json
dotnet run --project src/FactLineage.Cli -- project remove orders-api --yes --format json
dotnet run --project src/FactLineage.Cli -- search "login" --project my-api --format yaml
```

Set `FACTLINEAGE_HOME` to store the SQLite database and backups in a custom location. The default is `%LOCALAPPDATA%\FactLineage` with `factlineage.db` on Windows. `AIDOC_HOME` and existing `%LOCALAPPDATA%\AI Doc\aidoc.db` data remain supported for migration. JSON writes use `--file` or `--stdin`; `stdout` contains final results and failures use a stable error document on `stderr`. Supported output formats are `text` (the default indented JSON), `json`, and readable `yaml` (or `yml`).