---
name: factlineage-memory-sync
description: 'Use when planning, investigating, or implementing work that may change source code, tests, behavior, APIs, persistence, or output contracts in FactLineage projects. Query relevant FactLineage memory through the local CLI before implementation-oriented source searches or reads, and update memory only after implementation and all relevant validations pass.'
argument-hint: 'Describe the source change to make and the FactLineage project name.'
user-invocable: true
disable-model-invocation: false
---

# FactLineage Memory Synchronization

Keep FactLineage memory aligned with verified source-code changes.

## When to Use

- Planning or investigating work that may lead to a source-code or test change, even before the affected file or symbol is known.
- Implementing, fixing, or refactoring source code.
- Changing behavior, APIs, command-line arguments, persistence, or output contracts.
- Adding or changing tests that alter documented behavior.

Pure documentation, explanation, or metadata tasks that cannot lead to source-code or test changes do not require memory synchronization.

## Procedure

1. Before reading implementation files or running source text, regex, or symbol searches, determine whether the task may lead to a source-code or test change. If it may, enter the memory-first workflow. Preliminary reads are limited to the task context, applicable agent instructions, project metadata, and CLI documentation needed to run the query.

2. From the FactLineage repository root, identify the registered project using JSON output:

   ```powershell
   dotnet run --project standalone/src/FactLineage.Cli -- project list --format json
   ```

   Use the registered `projects.name` value, not a filesystem directory name or Git repository name. Then query relevant memory using the task's behavior or domain language; do not wait until a class or method is known:

   ```powershell
   dotnet run --project standalone/src/FactLineage.Cli -- search "<class, method, feature, or behavior>" --project <project-name> --format json
   ```

   If the target repository is not registered, add it with `project add` only when its path is known and modifying the local FactLineage database is authorized. Use the returned `name` value for every `--project` argument.

3. Use matching memory entries, code references, and current versions to identify the owning class, method, contract, and related tests. Only after reviewing the results may you inspect implementation code or run source searches. If the query succeeds but no result matches, continue with local investigation and plan a new memory entry for the changed component.

4. Implement the source-code change and complete all relevant validation, including focused tests and any required build, integration, or publish checks. Do not write a memory revision while validation is failing or incomplete.

5. After successful validation, synchronize the change through the CLI:

   - For an existing memory, append an immutable revision:

     ```powershell
   dotnet run --project standalone/src/FactLineage.Cli -- memory revise <memory-id> --file <revision.json> --format json
     ```

   - For a newly documented component, create a memory:

     ```powershell
   dotnet run --project standalone/src/FactLineage.Cli -- memory report --project <project-name> --file <memory.json> --format json
     ```

6. The revision or report must describe the verified behavior, affected classes and methods, relevant parameters or error contracts, and project-relative code references with correct line ranges. Set `createdBy` to the active agent or author.

7. Verify synchronization by retrieving the current record or history:

   ```powershell
   dotnet run --project standalone/src/FactLineage.Cli -- memory get <memory-id> --format json
   dotnet run --project standalone/src/FactLineage.Cli -- memory history <memory-id> --format json
   ```

   Confirm the expected current version, updated details, and source references. Preserve prior versions; never overwrite history outside `memory revise`.

## Query Failure Handling

- If the CLI cannot build or start, the database cannot be opened, or `FACTLINEAGE_HOME` (or legacy `AIDOC_HOME`) is inaccessible, record the failed preflight and continue with local investigation only when the source task can still be completed safely. Do not treat the failure as an empty memory result.
- If the CLI works but the project is not registered and registration is not authorized or its path is unknown, report the missing registration before continuing without memory results.
- If the project is registered and the query succeeds with no matches, continue with source investigation and plan a new memory report after validation.
- If a memory query fails for another reason, preserve and report the error; do not claim that memory was consulted successfully.
- After a verified source change, retry synchronization if the earlier failure may have been transient. If synchronization remains unavailable, explicitly report the pending memory update.

## Requirements

- For tasks that may change source code or tests, always query FactLineage before implementation-oriented source searches, implementation reads, or edits.
- Always update FactLineage after verified source-code changes.
- Use `--format json` for agent-facing CLI calls.
- Keep stdout machine-readable and rely on exit code plus `error.code` for failures.
- Never represent a failed or unavailable query as a successful query with no matching memory.
- Respect `FACTLINEAGE_HOME` when the project uses a non-default local data directory, with `AIDOC_HOME` supported as a legacy fallback.
