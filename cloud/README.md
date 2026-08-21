# AI Doc Cloud

AI Doc Cloud is the .NET 10 HTTP and MCP service described in the Azure three-day MVP design. PostgreSQL remains the system of record, while Azure AI Search provides keyword, vector, and semantic retrieval.

Agents new to the product should start with [docs/agent-introduction.md](docs/agent-introduction.md).

Agents deploying or operating the service should start with [docs/agent-deployment/00-index.md](docs/agent-deployment/00-index.md). That package contains machine-oriented invariants, decision routes, failure recovery, verification, and MCP knowledge-upload procedures.

For local VS Code installation with Microsoft Entra authentication, use [docs/local-vscode-mcp-configuration.md](docs/local-vscode-mcp-configuration.md).

## Identity model

The service has one user-assigned managed identity. Runtime calls use that identity end to end:

| Dependency | Authentication and authorization |
| --- | --- |
| Azure Database for PostgreSQL | Microsoft Entra database administrator; Npgsql refreshes an Entra token |
| Azure AI Search | Search Service Contributor and Search Index Data Contributor |
| Azure OpenAI | Cognitive Services OpenAI User |
| Azure Container Registry | AcrPull |

PostgreSQL password authentication and local authentication for Azure AI Search and Azure OpenAI are disabled in Bicep. The deployment contains no static cloud credentials or Container Apps secrets.

Inbound HTTP and MCP requests require a Microsoft Entra bearer token whose audience matches `apiAudience`. Create an Entra application registration that exposes this audience; the service validates tokens but does not need a client credential. A local developer can authenticate with `az login`, while another Azure workload should obtain the token through its own managed identity.

Memory writes persist two author dimensions: `actorId` is derived from trusted Entra `tid` plus `oid` (or `sub` fallback), while `agentName` is the caller-supplied display label. Legacy `createdBy` remains accepted and returned as an alias for `agentName`; it is not an authenticated identity.

## Projects

```text
cloud
  src/AiDoc.Cloud.Api       ASP.NET Core HTTP and MCP service
  tests/AiDoc.Cloud.Api.Tests
  infra/foundation.bicep    Azure resources and RBAC
  infra/postgres-administrator.bicep
  infra/app.bicep           Container App and runtime configuration
  infra/deploy.ps1          Phased deployment
```

## Configurable names

Edit [infra/deploy.parameters.json](infra/deploy.parameters.json) before deployment. Every Azure resource name and application-level Azure name is configurable:

- resource group and region
- managed identity
- Container Registry, Container Apps environment, and Container App
- Log Analytics workspace
- PostgreSQL server and database
- Azure AI Search service, index, and semantic configuration
- Azure OpenAI account, embedding deployment, SKU, and capacity
- image repository and tag
- Entra API audience

Registry, PostgreSQL, Search, and Azure OpenAI account names must be globally unique. The embedding model version must be available in the selected region.

## Deploy

Prerequisites are .NET 10, Azure CLI with Bicep, and an Azure identity that can create resources and role assignments in the target subscription.

```powershell
az login
Set-Location cloud
.\infra\deploy.ps1
```

The script performs four steps and stops immediately when an Azure CLI operation fails:

1. Deploys the foundation resources and managed identity role assignments.
2. Waits for PostgreSQL to become ready and configures the managed identity as its Entra administrator.
3. Builds the container remotely in ACR using the caller's Entra session.
4. Deploys the Container App with the managed identity and configuration values.

No local Docker daemon is required. Role assignment propagation can take several minutes after the first deployment; restart the Container App revision if startup initially receives an authorization response from an Azure dependency.

## Local build and test

```powershell
dotnet build cloud/AiDoc.Cloud.slnx
dotnet test cloud/AiDoc.Cloud.slnx
```

Running the API locally requires the `Cloud__*` configuration values used in [app.bicep](infra/app.bicep). `DefaultAzureCredential` uses the signed-in developer identity outside Azure.

## Endpoints

| Operation | Endpoint |
| --- | --- |
| Discover MCP and Entra configuration | `GET /.well-known/aidoc-mcp.json` |
| Process health | `GET /health` |
| Create project | `POST /v1/projects` |
| List projects | `GET /v1/projects` |
| Report memory | `POST /v1/projects/{projectId}/memories` |
| Revise memory | `POST /v1/memories/{memoryId}/versions` |
| Get memory | `GET /v1/memories/{memoryId}` |
| Search memories | `POST /v1/projects/{projectId}/search` |
| Submit or replace feedback | `PUT /v1/memories/{memoryId}/versions/{version}/feedback` |
| Delete caller feedback | `DELETE /v1/memories/{memoryId}/versions/{version}/feedback` |
| Get feedback summary | `GET /v1/memories/{memoryId}/versions/{version}/feedback-summary` |
| Rebuild search index | `POST /internal/reindex` |
| MCP Streamable HTTP | `/mcp` |

Except for `/health`, documentation, and instance discovery, all endpoints require an Entra bearer token. The MCP server exposes `create_project`, `list_projects`, `report_memory`, `search_memories`, `get_memory`, `submit_memory_feedback`, and `get_memory_feedback_summary`.

Feedback is scoped to an immutable memory version. Actor identity comes from the validated Entra token, not from tool or request input. Negative reasons `incorrect`, `stale`, and `missing_evidence` set `needsReview`; `irrelevant` records retrieval quality without asserting that the memory is wrong. Feedback does not change search ranking.

The Entra application must expose a delegated `access_as_user` scope. A signed-in developer can request a short-lived token with:

```powershell
az account get-access-token `
  --scope "<apiAudience>/access_as_user" `
  --query accessToken `
  --output tsv
```

For VS Code, set `AIDOC_CLOUD_MCP_URL` to the deployed `/mcp` URL and `AIDOC_CLOUD_TOKEN` to that short-lived token before starting the server configured in [.vscode/mcp.json](.vscode/mcp.json).