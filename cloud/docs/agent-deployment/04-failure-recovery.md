# Deployment Failure Recovery Matrix

## Agent contract

Classify by deployment stage. Collect the smallest discriminating evidence, fix one root cause, rerun its narrow validation, then rerun the orchestrator. Never continue to later stages after a failed stage.

## Find the failed resource

```powershell
az deployment operation group list `
  --resource-group <resource-group> `
  --name <deployment-name> `
  --query "[?properties.provisioningState=='Failed'].{resource:properties.targetResource.resourceName,type:properties.targetResource.resourceType,statusMessage:properties.statusMessage}" `
  -o json
```

Deployment names: `aidoc-foundation`, `postgres-entra-administrator`, and `aidoc-application`.

## Foundation failures

| Error | Meaning | Recovery |
| --- | --- | --- |
| OpenAI `InsufficientQuota` | Requested TPM exceeds remaining subscription quota | Lower configurable `embeddingCapacity`; rerun full ARM validation |
| OpenAI `InvalidResourceProperties` for SKU | Model/version exists but SKU unsupported in region | Validate another SKU, such as `GlobalStandard`, without also changing model/version |
| Search `InsufficientResourcesAvailable` | Region currently cannot create intended Search SKU | Validate all constrained services in another region |
| PostgreSQL `Version should be in: []` | Subscription/region cannot provision requested offering | Change to a fully validated region; do not blindly downgrade |

## PostgreSQL administrator failure

Symptom:

```text
AadAuthOperationCannotBePerformedWhenServerIsNotAccessible
```

Recovery: wait for `state == 'Ready'`, then deploy the administrator separately. If already ready, rerun the full script; both operations are idempotent.

## ACR build failure

Symptom:

```text
Unable to find fallback package folder 'D:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages'
```

Cause: host `obj/project.assets.json` entered the Linux build context after container restore.

Recovery:

1. Put `.dockerignore` in the exact `az acr build` context.
2. Exclude `bin/` and `obj/`.
3. Keep ACR output visible.
4. Rebuild with a new immutable tag.

Retrieve logs:

```powershell
$runId = az acr task list-runs --registry <acr> --query '[0].runId' -o tsv
az acr task logs --registry <acr> --run-id $runId --only-show-errors
```

## Container App failures

| Symptom | Cause | Recovery |
| --- | --- | --- |
| Deployment succeeds but behavior is old | Mutable image tag reused | Build with new tag; verify active template image and revision |
| Startup dependency 403 | RBAC missing or propagating | Verify role at exact resource scope, wait, restart/redeploy; never add key fallback |
| `libgssapi_krb5.so.2` warning | Optional Kerberos library absent | Ignore if token-based PostgreSQL initialization and queries succeed |
| Ephemeral Data Protection warning | Keys not persisted in container | Non-blocking for bearer-only API; reassess if cookies/protected state are added |

## Entra/API failures

| Symptom | Recovery |
| --- | --- |
| `consent_required` | Create `access_as_user`, token version 2, then preauthorize client in a second Graph PATCH |
| Token has scope but API 401 | Decode safe claims; v2 `aud` may be bare client ID; retain issuer/tenant checks |
| Anonymous business request returns 200 | Security regression: require auth on `/v1`, `/internal`, and `/mcp`; only `/health` is anonymous |

## Cleanup after regional failure

Delete only a dedicated deployment resource group. Wait for deletion before reusing names. Purge a soft-deleted OpenAI account only if its global name must be reused in another region.

## Stop conditions

Require user input when Service Tree ownership is ambiguous, resource-group ownership is unclear, role-assignment permission is unavailable, no region passes full validation, or minimum acceptable quota is unavailable.
