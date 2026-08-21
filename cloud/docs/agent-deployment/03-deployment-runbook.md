# Four-Stage Deployment Runbook

## Agent contract

Use `cloud/infra/deploy.ps1`. Do not reorder its phases. It is fail-fast and ARM-idempotent. After a failure, diagnose that phase, verify one correction, then rerun.

## Prerequisites

- PowerShell 7.
- Azure CLI with Bicep.
- Azure login in the intended tenant/subscription.
- Permission to create resources and role assignments.
- Validated local parameters.
- Configured Entra API application and audience.

Confirm context:

```powershell
az account show --query '{subscription:id,name:name,tenantId:tenantId,user:user.name}' -o json
```

Execute from repository root:

```powershell
.\cloud\infra\deploy.ps1 `
  -ParametersFile .\.local\factlineage-cloud\deploy.parameters.json
```

## Stage 1: Foundation

`foundation.bicep` creates the user-assigned identity, ACR, Log Analytics, Container Apps environment, PostgreSQL server/database/firewall, Search, OpenAI embedding deployment, and role assignments.

The orchestrator checks `$LASTEXITCODE` after every `az` command. `$ErrorActionPreference = 'Stop'` alone does not reliably turn native process failures into terminating PowerShell errors.

## Stage 2: PostgreSQL Entra administrator

ARM can finish server creation before PostgreSQL accepts Entra principal operations. Keep administrator configuration separate and wait for:

```powershell
az postgres flexible-server wait `
  --custom "state == 'Ready'"
```

Then deploy `postgres-administrator.bicep` with the managed identity principal ID. This prevents `AadAuthOperationCannotBePerformedWhenServerIsNotAccessible`.

## Stage 3: ACR remote build

`az acr build` avoids a local Docker dependency. Its build context is exactly:

```text
cloud
```

The Dockerfile is `cloud/src/FactLineage.Cloud.Api/Dockerfile`. Therefore `.dockerignore` must exist at the cloud root and exclude:

```text
bin/
obj/
```

A parent `.dockerignore` does not apply. Without this exclusion, host Windows `obj/project.assets.json` can overwrite Linux restore output and inject an invalid Visual Studio fallback package path.

Use an immutable image tag and retain the pushed image digest as evidence.

## Stage 4: Container App

`app.bicep` deploys external HTTPS ingress, user-assigned identity, identity-based ACR pull, no secrets, `Cloud__*` endpoint/name settings, and one-to-three replicas.

Resource endpoints and names are configuration, not credentials. Runtime SDKs acquire tokens.

## Rerun semantics

- Foundation updates incrementally.
- PostgreSQL wait returns immediately when ready.
- Reapplying the administrator is idempotent.
- ACR always builds when invoked; changed code needs a new tag.
- Container Apps creates a revision when image/template changes.

If changing region after a failed dedicated deployment, delete only its owned resource group, wait for deletion, and purge soft-deleted OpenAI only when reusing its global name:

```powershell
az group delete --name <resource-group> --yes --only-show-errors
az cognitiveservices account purge `
  --name <openai-name> `
  --resource-group <deleted-resource-group> `
  --location <old-region> `
  --only-show-errors
```

Never delete a shared or ambiguously owned resource group.

## Completion criteria

ARM success is not sufficient. Continue with `05-verification-and-operations.md` and collect active image, ready revision, health, authorization, semantic search, and MCP evidence.
