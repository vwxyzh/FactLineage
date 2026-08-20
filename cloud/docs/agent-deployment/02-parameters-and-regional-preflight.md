# Parameters, Naming, Region, and Quota Preflight

## Agent contract

Never deploy from the checked-in placeholder file. Create a Git-ignored local copy, replace every `<your-...>` value, validate names and regional capabilities, then invoke the orchestrator.

## Local parameter workflow

Recommended path:

```text
<repo>/.local/aidoc-cloud/deploy.parameters.json
```

Confirm ignore behavior before writing real names or IDs:

```powershell
git check-ignore -v .local/aidoc-cloud/deploy.parameters.json
```

Parameter categories:

| Category | Examples | Rule |
| --- | --- | --- |
| Globally unique | ACR, PostgreSQL, Search, OpenAI | Check availability; obey service naming syntax |
| Resource-group unique | Container App, environment, identity, workspace | Follow local naming policy |
| Logical | database, index, semantic configuration | Keep stable across image revisions |
| Model contract | model, version, dimensions, SKU, capacity | Must pass regional and quota validation |
| Identity contract | `apiAudience` | Must match the App Registration |
| Revision | image tag | Must be immutable for changed code |

## Parameter reasoning

- `location`: every resource uses one region; validate all constrained services together.
- `embeddingDimensions`: must match OpenAI output and Search vector field. A change requires index recreation or migration.
- `embeddingCapacity`: thousands of tokens per minute; request no more than available quota.
- `embeddingSkuName`: regional model deployment SKU. `GlobalStandard` was required for the validated `westus3` deployment.
- `apiAudience`: normally `api://<client-id>`; v2 token `aud` may be bare client ID.
- `imageTag`: use commit SHA, timestamp, or unique build ID. Do not reuse a mutable deployment tag.

## Full regional validation

Public support tables are insufficient because provisioning can be subscription-specific. Create a disposable, Git-ignored Bicep probe containing:

1. PostgreSQL with exact version and SKU.
2. Search with exact SKU and semantic setting.
3. OpenAI account plus exact model, version, deployment SKU, and capacity.

Validate without creating resources:

```powershell
az deployment group validate `
  --resource-group <preflight-resource-group> `
  --template-file <local-region-probe.bicep> `
  --parameters location=<candidate-region> suffix=<unique-suffix> `
  --only-show-errors
```

## Observed failures and decisions

| Error | Observation | Correct decision |
| --- | --- | --- |
| Search `InsufficientResourcesAvailable` | `eastus2` lacked new Basic capacity | Validate a different complete region |
| PostgreSQL `Version should be in: []` | Subscription could not provision requested offering | Treat empty list as regional/subscription restriction |
| OpenAI SKU `Standard` unsupported | Model existed but deployment SKU failed | Validate `GlobalStandard` with same model/version |
| OpenAI `InsufficientQuota` | Requested 120K TPM, only 60K remained | Parameterize capacity and request within quota |

Verified evidence from 2026-08-20:

```json
{
  "location": "westus3",
  "embeddingModelName": "text-embedding-3-small",
  "embeddingModelVersion": "1",
  "embeddingSkuName": "GlobalStandard",
  "embeddingCapacity": 30,
  "embeddingDimensions": 1536
}
```

Revalidate these values for every subscription and later deployment.

## Useful checks

```powershell
az account show --query '{subscription:id,tenantId:tenantId,user:user.name}' -o json
az acr check-name --name <acr-name> -o json
az resource list --resource-group <resource-group> -o table
az bicep build --file cloud/infra/foundation.bicep --stdout | Out-Null
```

ARM validation is more discriminating than general location lists when subscription provisioning restrictions apply.

## Completion criteria

Proceed only when account context is explicit, local JSON parses, no placeholders remain, all Bicep compiles, the complete region probe succeeds, the API application exists, the caller can assign roles, and the image tag is unique.
