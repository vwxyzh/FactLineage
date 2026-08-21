# FactLineage Command-Line Reference

This document describes the arguments and options supported by the standalone FactLineage CLI.

## Invocation

Use the installed executable:

```powershell
factlineage <command> [arguments] [options]
```

Notation used in this reference:

- `<value>` is required.
- `[value]` is optional.
- `...` means an option can be repeated.
- `A | B` means the options are mutually exclusive.

## Common options

| Option | Values | Default | Description |
| --- | --- | --- | --- |
| `--format <format>` | `text`, `json`, `yaml`, `yml` | `text` | Selects the output format. `text` is indented JSON, `json` is compact JSON, and `yaml`/`yml` is readable YAML. |
| `--help` | None | Off | Returns help for the current command. It can be used after a command path. |

Command help is also available through the `help` command:

```powershell
factlineage help
factlineage help project add --format json
factlineage memory report --help --format yaml
```

## Environment

| Variable | Description |
| --- | --- |
| `FACTLINEAGE_HOME` | Overrides the directory containing `factlineage.db`, models, and backups. On Windows, the default is `%LOCALAPPDATA%\FactLineage`. |
| `AIDOC_HOME` | Legacy fallback used only when `FACTLINEAGE_HOME` is unset. Existing `aidoc.db` databases remain supported. |

When neither variable is set, FactLineage reuses `%LOCALAPPDATA%\AI Doc\aidoc.db` if that legacy database exists; otherwise it creates `%LOCALAPPDATA%\FactLineage\factlineage.db`.

## Project commands

### `project add`

Registers an existing local project directory.

```powershell
factlineage project add --name <name> --path <path> [--remote-url <url>] [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `--name <name>` | Yes | Unique project name in the local FactLineage database. |
| `--path <path>` | Yes | Existing project directory. FactLineage stores its normalized absolute path. |
| `--remote-url <url>` | No | Optional Git remote URL recorded with the project. |

Example:

```powershell
factlineage project add --name orders-api --path D:\code\orders-api --format json
```

### `project update`

Updates one registered project without changing its project ID. Provide at least one change option.

```powershell
factlineage project update <name> [--new-name <name>] [--path <path>] [--remote-url <url> | --clear-remote-url] [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `<name>` | Yes | Current name of the registered project. |
| `--new-name <name>` | No | New unique project name. |
| `--path <path>` | No | New existing project directory. |
| `--remote-url <url>` | No | Sets or replaces the Git remote URL. |
| `--clear-remote-url` | No | Removes the stored remote URL. Cannot be combined with `--remote-url`. |

Example:

```powershell
factlineage project update orders-api --new-name orders-service --path D:\code\orders-service
```

### `project list`

Lists all projects, or selected projects in the order requested.

```powershell
factlineage project list [--name <name> ...] [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `--name <name>` | No | Selects a project by name. Repeat to select multiple projects. Omit to list all projects. |

Example:

```powershell
factlineage project list --name orders-api --name shared-lib --format json
```

If any requested project does not exist, the command fails without returning a partial list.

### `project show`

Returns one registered project.

```powershell
factlineage project show <name> [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `<name>` | Yes | Registered project name. |

### `project remove`

Removes a project and all associated memories, versions, search documents, and embeddings. It does not delete source files.

```powershell
factlineage project remove <name> --yes [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `<name>` | Yes | Registered project name. |
| `--yes` | Yes | Explicit confirmation for the destructive database operation. |

## Memory commands

### `memory report`

Creates a memory and immutable version 1 from a JSON document.

```powershell
factlineage memory report --project <name> (--file <path> | --stdin) [--allow-missing-references] [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `--project <name>` | Yes | Single project that owns the new memory. |
| `--file <path>` | Conditional | Reads the JSON document from a file. Use exactly one of `--file` or `--stdin`. |
| `--stdin` | Conditional | Reads the JSON document from standard input. Use exactly one of `--stdin` or `--file`. |
| `--allow-missing-references` | No | Allows code reference files that do not currently exist. It does not allow paths outside the project root. |

Example:

```powershell
factlineage memory report --project orders-api --file .\memory.json --format json
```

### `memory import`

Recursively imports `.json` memory report documents from a directory.

```powershell
factlineage memory import --project <name> --directory <path> [--allow-missing-references] [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `--project <name>` | Yes | Single project that owns every imported memory. |
| `--directory <path>` | Yes | Existing directory to scan recursively for `.json` files. |
| `--allow-missing-references` | No | Allows referenced files that do not currently exist within the project. |

Files are processed in stable relative-path order. Each file uses a separate transaction. An invalid file is reported in `failures`, while valid files continue to import.

### `memory revise`

Appends a new immutable version to an existing memory.

```powershell
factlineage memory revise <memory-id> (--file <path> | --stdin) [--allow-missing-references] [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `<memory-id>` | Yes | Existing memory UUID. |
| `--file <path>` | Conditional | Reads the revision JSON from a file. Use exactly one input source. |
| `--stdin` | Conditional | Reads the revision JSON from standard input. Use exactly one input source. |
| `--allow-missing-references` | No | Allows code reference files that do not currently exist within the project. |

### `memory get`

Returns a memory and its current version.

```powershell
factlineage memory get <memory-id> [--format <format>]
```

### `memory export`

Exports the current version as a reusable `memory report` document.

```powershell
factlineage memory export <memory-id> [--format <format>]
```

The returned `document` contains `type`, `title`, `summary`, `details`, `codeReferences`, and `createdBy`.

### `memory history`

Returns every immutable version of a memory in version order.

```powershell
factlineage memory history <memory-id> [--format <format>]
```

## Memory JSON input

`memory report` and `memory import` accept this structure:

```json
{
  "type": "feature",
  "title": "Order submission",
  "summary": "Validates and submits a customer order.",
  "details": {
    "endpoint": "POST /orders"
  },
  "codeReferences": [
    {
      "path": "src/Orders/OrderService.cs",
      "symbol": "OrderService.SubmitAsync",
      "startLine": 20,
      "endLine": 68
    }
  ],
  "createdBy": "coding-agent"
}
```

| Field | Required | Constraints |
| --- | --- | --- |
| `type` | Yes | `feature`, `api`, or `decision`. |
| `title` | Yes | Non-empty string. |
| `summary` | Yes | Non-empty string. |
| `details` | No | Any valid JSON value. |
| `codeReferences` | No | Array of code reference objects. Defaults to an empty array. |
| `createdBy` | Yes | Non-empty agent or author identifier. |

`memory revise` uses the same structure without `type` and `title`:

```json
{
  "summary": "Now validates inventory before submission.",
  "details": {
    "validation": "inventory"
  },
  "codeReferences": [],
  "createdBy": "coding-agent"
}
```

Code reference constraints:

- `path` is required and must be relative to the registered project root.
- `symbol` is optional and can be `null`.
- `startLine` and `endLine` must be positive integers.
- `endLine` must be greater than or equal to `startLine`.
- Referenced files must exist unless `--allow-missing-references` is set.
- Unknown JSON fields are rejected.
- One input document cannot exceed 1 MB.

## Search command

Performs hybrid FTS5 keyword and local semantic retrieval.

```powershell
factlineage search <query> (--project <name> ... | --all-projects) [--type <type>] [--limit <count>] [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `<query>` | Yes | Natural-language query or exact path, symbol, API, or code term. Quote values containing spaces. |
| `--project <name>` | Conditional | Project to search. Repeat for multiple projects. Cannot be combined with `--all-projects`. |
| `--all-projects` | Conditional | Searches all projects visible when the command starts. Cannot be combined with `--project`. |
| `--type <type>` | No | Filters results to `feature`, `api`, or `decision`. |
| `--limit <count>` | No | Global result limit from 1 through 100. Defaults to `10`. |

Exactly one search scope is required: one or more `--project` options, or `--all-projects`.

Examples:

```powershell
factlineage search "How is authentication implemented?" --project web-api --limit 5 --format json
factlineage search "deprecated endpoint" --project web-api --project shared-lib --type api
factlineage search "MemoryService.Search" --all-projects
```

Each result includes `projectId`, `projectName`, the memory, its current version, and a normalized relevance `score`.

## Embedding commands

### `embedding model download`

Downloads the `multilingual-e5-small` ONNX model and SentencePiece tokenizer into `FACTLINEAGE_HOME`.

```powershell
factlineage embedding model download [--format <format>]
```

### `embedding backfill`

Generates missing or outdated embeddings for all memory versions in one project.

```powershell
factlineage embedding backfill --project <name> [--format <format>]
```

| Parameter | Required | Description |
| --- | --- | --- |
| `--project <name>` | Yes | Project whose memory versions will be processed. |

Run `embedding model download` before backfill. A memory write can succeed without the model; its `embeddingStatus` is then `pending` until backfill completes.

## Maintenance commands

### `doctor`

Checks SQLite integrity, registered project paths, and embedding provider availability. It does not modify data.

```powershell
factlineage doctor [--format <format>]
```

### `backup`

Creates a consistent SQLite backup under `FACTLINEAGE_HOME\backups`.

```powershell
factlineage backup [--format <format>]
```

The command returns the created `backupPath`.

### `version`

Returns the CLI version and output schema version.

```powershell
factlineage version [--format <format>]
```

## Output contract

Successful commands write one envelope to standard output:

```json
{
  "schemaVersion": 1,
  "data": {}
}
```

Failures write one stable error envelope to standard error:

```json
{
  "schemaVersion": 1,
  "error": {
    "code": "PROJECT_NOT_FOUND",
    "message": "Project 'orders-api' does not exist",
    "details": {}
  }
}
```

Automation should branch on the process exit code and `error.code`, not on `error.message`.

| Exit code | Meaning |
| --- | --- |
| `0` | Success. |
| `2` | Invalid command arguments, output format, JSON, or an out-of-project code reference. |
| `3` | Domain or validation failure, such as a missing project, missing memory, or required confirmation. |
| `4` | External dependency failure, such as file, network, Git, or model access. |
| `5` | Database or unhandled internal failure. |