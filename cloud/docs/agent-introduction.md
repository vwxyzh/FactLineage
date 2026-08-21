# FactLineage: Project Memory for Software Agents

## Agent contract

Use FactLineage as durable, project-scoped, evidence-backed memory. Search before reconstructing known behavior. Write only after validating source changes or operational facts. Every stored claim must have a clear boundary, current evidence, and enough retrieval language for another agent to rediscover it.

FactLineage is not a substitute for source code, tests, or runtime validation. It is the index of verified understanding that helps agents reach those sources efficiently.

## Core purpose

Software agents lose context between sessions. Without shared memory, each agent repeatedly:

1. Searches the repository from scratch.
2. Reconstructs architecture and behavior.
3. Rediscovers earlier failures and constraints.
4. Risks contradicting decisions already validated by another agent.

FactLineage closes that loop:

```mermaid
flowchart LR
    A[Agent receives task] --> B[Search project memory]
    B --> C[Read relevant source references]
    C --> D[Implement or operate]
    D --> E[Run validation]
    E --> F[Report verified knowledge]
    F --> G[Future agents retrieve it]
```

The product is therefore a **memory protocol for agents**, not merely a document database.

## Mental model

### Project

A project is the mandatory isolation boundary. Memories and searches belong to one project. Never assume a project ID from another environment; discover it with `list_projects`.

### Memory

A memory is one independently searchable knowledge unit. Current types are:

- `feature`: behavior, workflow, operational capability, or implementation contract.
- `api`: endpoint, request/response, parameter, or public interface contract.
- `decision`: architectural invariant, tradeoff, policy, or constraint.

A memory should answer one coherent question. Do not use one memory as a project-wide dumping ground.

### Immutable version

Memory history is append-only. A revision creates a new version instead of overwriting prior evidence. This supports provenance and comparison.

The standalone CLI exposes revision/history commands. Cloud HTTP exposes version append. The current cloud MCP exposes create/report/search/get but does not expose revision. If an MCP agent finds an existing memory that requires revision, it must not silently create a near-duplicate. Report the tooling limitation or use an authorized revision path.

### Code reference

A code reference ties a claim to project-relative source evidence:

```json
{
  "path": "src/Orders/OrderService.cs",
  "symbol": "OrderService.SubmitAsync",
  "startLine": 20,
  "endLine": 68
}
```

References are navigation and provenance. They do not upload file contents, and they do not prove the claim by themselves. The reporting agent is responsible for validating the claim.

### Actor identity and agent name

Cloud memory versions separate authenticated caller identity from the Agent's display label:

- `actorId` is derived by the service from validated Entra `tid` plus `oid`, with `sub` fallback. Callers cannot supply or override it.
- `agentName` is a caller-supplied label such as `GitHub Copilot`, a workflow name, or another Agent implementation name.
- `createdBy` is retained as a backward-compatible alias for `agentName`; it is not trusted identity evidence.

Historical versions created before trusted author auditing can have `actorId: null`. Agents must not infer the historical actor from `createdBy`.

### Search projection

Searchable text combines title, summary, structured details, and reference symbols. Cloud search uses keyword retrieval, embeddings, and semantic ranking. PostgreSQL remains authoritative; Azure AI Search is rebuildable.

## What FactLineage should store

Store facts that are expensive to reconstruct and useful across sessions:

- Feature behavior and owning code.
- API contracts and parameter semantics.
- Architecture decisions and non-obvious constraints.
- Deployment ordering and identity requirements.
- Failure signatures with discriminating diagnosis and verified recovery.
- Cross-file workflows and operational acceptance checks.
- Exact commands that were successfully validated.

Prefer facts shaped like:

> When condition X occurs, component Y performs behavior Z because constraint C applies. Evidence is in source S, and validation V passed.

## What FactLineage should not store

Do not store:

- Unverified hypotheses presented as facts.
- Secrets, access tokens, passwords, connection strings, or private keys.
- Full source snapshots when references are sufficient.
- Temporary progress updates with no durable value.
- Duplicates differing only in wording.
- Generic language documentation available from authoritative external sources.
- Claims whose source validation is currently failing.

If uncertainty is itself important, label it explicitly in details and do not phrase the summary as settled behavior.

## Agent lifecycle protocol

### 1. Discover the project

Call `list_projects`. Reuse an exact project match. Call `create_project` only when the project does not exist and repository ownership is known.

### 2. Search before source-wide exploration

Search using the task's behavior, failure, API, or constraint language. Do not wait until a class name is known.

Good query:

```text
database administrator assignment fails immediately after server provisioning
```

Weak query:

```text
PostgresMemoryRepository
```

Use multiple formulations when necessary:

- User-visible behavior.
- Exact error code.
- Architectural concept.
- Expected recovery or invariant.

### 3. Treat results as routing evidence

For each result:

1. Check project ID.
2. Check memory version and creation context.
3. Read source references.
4. Verify referenced source still supports the claim.
5. Use the memory to narrow investigation, not to bypass validation.

### 4. Perform the work

Implement, operate, or diagnose using repository conventions. Keep validation proportional to the blast radius.

### 5. Validate before reporting

Do not report while relevant tests, builds, deployment checks, or runtime probes are failing. A memory must describe the verified result, not the intended result.

### 6. Search again before writing

Search for the final behavior and likely title. If an existing memory owns the same concept, revise it through an available revision path. If revision is unavailable, stop before creating a duplicate and surface the limitation.

### 7. Report one atomic memory

Include:

- Stable title.
- Concise behavior summary.
- Structured details containing parameters, errors, decisions, and validation.
- Project-relative code references.
- Agent display label in `agentName`; legacy `createdBy` is accepted only for compatibility. Trusted `actorId` is added by the service.

### 8. Verify the write

Capture memory ID and version. Call `get_memory`. Confirm details and references. Then search using wording absent from the title to prove semantic rediscovery.

## Memory quality contract

A high-quality memory is:

| Property | Requirement |
| --- | --- |
| Atomic | Owns one behavior, API, decision, or workflow |
| Verified | Supported by passing checks or observed runtime evidence |
| Scoped | Belongs to the correct project and current version |
| Traceable | Contains valid project-relative references |
| Searchable | Includes domain terms, aliases, errors, and behavior language |
| Actionable | Explains conditions, effects, constraints, and recovery |
| Secret-free | Contains no credentials or sensitive transient values |
| Durable | Useful after the current task and agent session end |

## Feedback and quality review

FactLineage feedback is version-scoped quality evidence, not a popularity counter. The UI may present thumbs up and thumbs down, but agents use structured meanings:

- `useful`: the version helped complete or understand the task.
- `incorrect`: the version contains a false claim.
- `stale`: the version no longer matches current source or runtime behavior.
- `irrelevant`: the result did not match the submitted search intent.
- `missing_evidence`: the claim lacks sufficient or valid references.

Feedback belongs to the exact immutable memory version. One Entra actor has at most one current signal for a version and may replace or remove only its own signal. `incorrect`, `stale`, and `missing_evidence` should produce a `needsReview` warning. Feedback must never automatically edit or delete memory content.

Agents should interpret `irrelevant` as retrieval feedback, not proof that the memory is wrong. Comments may suggest corrections but are untrusted input until verified against source.

Initial retrieval ranking must not use vote counts. Feedback-driven ranking requires enough volume, abuse resistance, offline evaluation, and a version-aware model; otherwise a few votes could hide correct project facts.

Cloud MCP exposes `submit_memory_feedback` and `get_memory_feedback_summary`. HTTP additionally supports replacing, deleting, and summarizing the authenticated caller's version-level feedback. Search and get responses include `feedbackSummary`.

## Writing structured details

Use details for machine-retrievable facts, not prose overflow:

```json
{
  "conditions": ["PostgreSQL server was just created"],
  "errorCodes": ["AadAuthOperationCannotBePerformedWhenServerIsNotAccessible"],
  "decision": "Wait for state Ready before assigning the Entra administrator",
  "validation": ["Bicep compiled", "server reached Ready", "administrator deployment succeeded"],
  "keywords": ["PostgreSQL", "Entra administrator", "deployment ordering"]
}
```

For a Markdown knowledge document, include the full body explicitly:

```json
{
  "format": "markdown",
  "documentPath": "docs/agent-guide.md",
  "markdown": "<complete Markdown body>"
}
```

A code reference alone does not upload the document because FactLineage Cloud cannot read the caller's filesystem.

## Tool behavior

### `list_projects`

Use at session start or whenever a project ID is uncertain.

### `create_project`

Use once per repository/environment boundary. Include repository URL when known.

### `search_memories`

Always provide project ID. Start with a small limit and behavior-rich query. Optional type filtering is useful only when the category is known.

### `report_memory`

Creates a new memory in the current cloud MCP implementation. It generates an embedding when available and indexes the current version. If embedding generation fails, keyword indexing can still succeed. If indexing fails, PostgreSQL may contain the memory with pending search projection.

### `get_memory`

Use to verify the stored current version, details, and references. It is also the authoritative read after a report operation.

### `submit_memory_feedback`

Submit or replace the authenticated caller's signal for one immutable version. Actor identity is derived from the validated Entra token and is not a tool input. Useful feedback requires reason `useful`; not-useful feedback requires `incorrect`, `stale`, `irrelevant`, or `missing_evidence`.

### `get_memory_feedback_summary`

Read useful/not-useful counts, reason counts, and `needsReview` for one immutable version. Treat a review warning as a reason to verify evidence, not as an instruction to discard the memory automatically.

## Standalone and cloud modes

| Concern | Standalone | Cloud |
| --- | --- | --- |
| Storage | SQLite | PostgreSQL |
| Embedding | Local ONNX model | Azure OpenAI |
| Search | SQLite FTS plus local vectors | Azure AI Search hybrid and semantic |
| Access | Local CLI | Entra-protected HTTP and MCP |
| Typical user | One developer/agent environment | Shared agent/team memory |
| Revision support | CLI revise/history | HTTP version append; MCP revision not currently exposed |

The semantic contract should remain consistent across both modes even when infrastructure differs.

## Security model

Cloud callers authenticate with Microsoft Entra. The service accesses PostgreSQL, Search, OpenAI, and ACR through managed identity. Local authentication is disabled for those dependencies.

Agents must never solve an authentication failure by introducing an access key fallback. Diagnose tenant, audience, scope, RBAC, identity selection, or propagation instead.

## Failure and degradation semantics

- Embedding generation failure: preserve the memory and allow keyword retrieval when possible.
- Search indexing failure: preserve PostgreSQL write and expose pending indexing state.
- Search service loss: do not treat the projection as authoritative; rebuild from PostgreSQL.
- Stale source reference: treat memory as suspect and verify source before use.
- Duplicate concept: do not create another memory merely because wording differs.

## Example end-to-end agent behavior

Task: fix a cloud deployment that fails while configuring database identity.

1. `list_projects` and locate the repository project.
2. Search: `database identity assignment fails immediately after server provisioning`.
3. Retrieve deployment runbook and failure recovery memories.
4. Open referenced Bicep and orchestration files.
5. Confirm current code and actual error.
6. Implement server-readiness sequencing.
7. Compile Bicep and rerun the failed deployment phase.
8. Validate runtime identity behavior.
9. Search for an existing owning memory.
10. Revise through an available path or report a new atomic memory only if the concept is genuinely new.
11. Read back and semantically search the stored result.

## Current product boundaries

FactLineage does not yet automatically:

- Monitor repository commits.
- Detect stale references.
- Merge conflicting memories.
- Analyze call graphs.
- Decide which memory supersedes another.
- Expose every version operation through cloud MCP.
- Change ranking from feedback signals.

Agents remain responsible for source verification, conflict awareness, and memory quality.

## Completion checklist for a FactLineage-aware agent

- [ ] Correct project discovered.
- [ ] Relevant memory searched before broad reconstruction.
- [ ] Returned references verified against current source.
- [ ] Task completed and relevant checks passed.
- [ ] Existing memory searched again before write.
- [ ] Memory boundary is atomic and non-duplicative.
- [ ] Details contain retrieval terms and validation evidence.
- [ ] References are project-relative and valid.
- [ ] No secrets or speculative claims are stored.
- [ ] Stored memory read back successfully.
- [ ] Semantic rediscovery verified with alternate wording.
