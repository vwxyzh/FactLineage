# Post-Deployment Verification and Operations

## Agent contract

Do not report success until control-plane, identity, data-plane, and MCP checks pass. Avoid destructive test data; use nonexistent IDs and empty searches.

## 1. Deployment phases

```powershell
az deployment group show -g <rg> -n factlineage-foundation --query properties.provisioningState -o tsv
az deployment group show -g <rg> -n postgres-entra-administrator --query properties.provisioningState -o tsv
az deployment group show -g <rg> -n factlineage-application --query properties.provisioningState -o tsv
```

All must be `Succeeded`.

## 2. Active Container App revision

```powershell
az containerapp show -g <rg> -n <app> --query "{runningStatus:properties.runningStatus,latestRevisionName:properties.latestRevisionName,latestReadyRevisionName:properties.latestReadyRevisionName,image:properties.template.containers[0].image,fqdn:properties.configuration.ingress.fqdn}" -o json
```

Require `Running`, latest equals latest-ready, and image contains the intended immutable tag.

## 3. Local authentication disabled

Query deployed state and require:

- Search `disableLocalAuth == true`.
- OpenAI `properties.disableLocalAuth == true`.
- PostgreSQL `activeDirectoryAuth == Enabled`, `passwordAuth == Disabled`.
- ACR `adminUserEnabled == false`.

Do not infer this only from Bicep.

## 4. Health and auth boundary

`GET /health` must return 200 `Healthy`. A business endpoint without a token must return 401.

Acquire delegated token:

```powershell
$scope = 'api://<client-id>/access_as_user'
$token = az account get-access-token --scope $scope --query accessToken -o tsv --only-show-errors
$headers = @{ Authorization = "Bearer $token" }
```

Call `GET /v1/memories/00000000-0000-0000-0000-000000000000`. Expected: authenticated 404, proving JWT validation without data writes.

## 5. Search index initialization

Acquire an Entra token for `https://search.azure.com`, then GET:

```text
https://<search>.search.windows.net/indexes/<index>?api-version=2025-09-01
```

Require eight fields, one semantic configuration, and one vector profile. Never use a Search admin key.

## 6. No-write semantic search

POST a query to a random project ID. Expected: success with zero results. This proves inbound Entra validation plus outbound managed identity calls to Azure OpenAI and Azure AI Search.

```powershell
$projectId = [guid]::NewGuid()
$body = @{ query = 'managed identity semantic search'; limit = 10 } | ConvertTo-Json
Invoke-RestMethod -Method Post `
  -Uri "$base/v1/projects/$projectId/search" `
  -Headers ($headers + @{ 'Content-Type' = 'application/json' }) `
  -Body $body
```

## 7. MCP handshake

POST authenticated `initialize` to `/mcp` with Accept `application/json, text/event-stream`. Expected: 200, `text/event-stream`, and `protocolVersion`.

`tools/list` must contain:

- `create_project`
- `list_projects`
- `report_memory`
- `search_memories`
- `get_memory`

## 8. Logs

```powershell
az containerapp logs show -g <rg> -n <app> --tail 100 --format text
```

Require application startup on port 8080 and no PostgreSQL schema, Search initialization, or repeated MSI auth failures. Never log tokens, full memory bodies, or credentials.

## 9. Updates and repair

For code updates: run Release tests, choose a new immutable tag, deploy, verify active image/revision, then repeat health/auth/search/MCP checks.

For index repair, call authenticated `POST /internal/reindex`; PostgreSQL remains authoritative.

## 10. Cleanup

Resources are billable. Remove a dedicated instance with:

```powershell
az group delete --name <resource-group> --yes
```

Purge soft-deleted OpenAI only if reusing its global name. Never delete ambiguously owned resources.
