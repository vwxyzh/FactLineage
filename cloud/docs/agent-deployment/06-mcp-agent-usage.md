# MCP Installation and Agent Usage

## Agent contract

Use MCP for AI Doc business operations. Do not write directly to PostgreSQL. Discover project IDs with `list_projects`; do not trust IDs copied from another environment.

## Tool surface

| Tool | Purpose | Inputs |
| --- | --- | --- |
| `create_project` | Create project scope | unique name, optional repository URL |
| `list_projects` | Discover project IDs | none |
| `report_memory` | Store immutable memory and search projection | project ID, type, title, summary, details, references, author |
| `search_memories` | Hybrid semantic search in one project | project ID, query, optional type, limit |
| `get_memory` | Read current memory and references | memory ID |

## Entra-authenticated local bridge

The server requires a bearer token but does not expose MCP OAuth discovery metadata. A fixed HTTP header expires. The validated VS Code installation uses a stdio bridge that acquires a fresh Azure CLI token on every start.

Install a pinned bridge in a Git-ignored directory:

```powershell
npm install `
  --prefix .local/aidoc-cloud/mcp-client `
  --save-exact `
  mcp-remote@0.1.38
```

The launcher must:

1. Request `api://<client-id>/access_as_user` using `az account get-access-token`.
2. Keep the token only in a child-process environment variable.
3. Start `mcp-remote` with `--transport http-only`.
4. Send `Authorization: Bearer <token>` via environment-expanded header.
5. Remove the environment variable on exit.

Never write tokens to `mcp.json`, `.env`, logs, or shell history.

Register the launcher in the VS Code user profile with `code --add-mcp`. The stdio command should run:

```text
pwsh -NoProfile -File <absolute-path-to-start-mcp.ps1>
```

Do not edit undocumented VS Code profile storage directly.

## Bridge validation

Use a persistent process; a one-shot pipeline may close stdin before the proxy responds. Send:

1. `initialize` with protocol `2025-06-18`.
2. `notifications/initialized`.
3. `tools/list`.

Require server `AiDoc.Cloud.Api` and all five tools.

## Project bootstrap

1. Call `list_projects`.
2. Reuse an exact existing project match.
3. Otherwise call `create_project` once.
4. Call `list_projects` again and persist the returned ID only as local environment metadata.

Validated evidence, not a portable default:

```json
{
  "id": "8b851503-a6ed-4de6-ab75-5798e005c764",
  "name": "aidoc",
  "repositoryUrl": "https://github.com/vwxyzh/aidoc.git"
}
```

## Uploading agent knowledge

The MCP does not upload file attachments. `report_memory.details` accepts arbitrary JSON, so upload the complete Markdown body explicitly:

```json
{
  "type": "decision",
  "title": "AI Doc Cloud identity contract",
  "summary": "Separates inbound Entra authentication from outbound managed identity and disables local Azure authentication.",
  "details": {
    "format": "markdown",
    "documentPath": "cloud/docs/agent-deployment/01-identity-and-entra.md",
    "markdown": "<complete Markdown document>",
    "keywords": ["managed identity", "Entra", "RBAC", "no access keys"]
  },
  "codeReferences": [
    {
      "path": "cloud/docs/agent-deployment/01-identity-and-entra.md",
      "symbol": "Agent contract",
      "startLine": 1,
      "endLine": 20
    }
  ],
  "createdBy": "<agent-name>"
}
```

This stores the body in PostgreSQL JSONB and includes it in normalized text and embeddings. A code reference alone does not upload the file because the cloud service cannot read the caller's filesystem.

## Memory boundaries

One memory should represent one independently searchable unit:

- Use `decision` for invariants and architecture choices.
- Use `feature` for executable workflows and operational capabilities.
- Include exact error codes, resource types, role names, and commands in `details`.
- Include the document and controlling source files as code references.
- Use project-relative paths and valid line ranges.
- Set `createdBy` to the active agent.

## Search before report

Search the cloud project using behavior and error language before reporting. If a memory already owns the concept, revise it when revision tooling is available. Otherwise choose a title and boundary that do not duplicate an existing unit.

## Upload verification

After each `report_memory`:

1. Capture returned memory ID and version.
2. Call `get_memory` and verify `details.markdown` is complete.
3. Run a semantic query using terminology not present in the title.
4. Confirm the memory and code references are returned.

## Session recovery

If startup reports `consent_required`, verify scope and Azure CLI preauthorization. If login expired, perform interactive `az login` for the tenant and scope. Never ask a user to paste an access token into chat.
