# Identity and Microsoft Entra Contract

## Agent contract

Keep two identity planes separate:

- **Inbound:** users, IDE agents, or workloads call HTTP/MCP using an Entra token for the AI Doc API application.
- **Outbound:** Container Apps calls Azure dependencies with one user-assigned managed identity through `DefaultAzureCredential`.

Never add an access-key fallback.

## Outbound identity and RBAC

| Target | Role/configuration | Purpose |
| --- | --- | --- |
| ACR | `AcrPull` | Pull runtime image |
| Search | `Search Service Contributor` | Create/update index |
| Search | `Search Index Data Contributor` | Write/query documents |
| Azure OpenAI | `Cognitive Services OpenAI User` | Generate embeddings |
| PostgreSQL | Entra administrator | Connect with database token and initialize schema |

Mandatory local-auth settings:

- ACR `adminUserEnabled: false`.
- Search `disableLocalAuth: true`.
- OpenAI `disableLocalAuth: true`.
- PostgreSQL `activeDirectoryAuth: Enabled`, `passwordAuth: Disabled`.

`Program.cs` selects the user-assigned identity by `ManagedIdentityClientId`. Npgsql uses a periodic password provider to obtain an ephemeral token for `https://ossrdbms-aad.database.windows.net/.default`. Never persist or log that token.

## Inbound App Registration

Required state:

| Property | Value |
| --- | --- |
| Sign-in audience | `AzureADMyOrg` |
| Application ID URI | `api://<application-client-id>` |
| Access token version | `2` |
| Delegated scope | `access_as_user` |
| Client credential | None |

Microsoft-owned tenants may require `serviceManagementReference` during `az ad app create`. Query owned applications for existing references and require an explicit Service Tree ownership choice. Never guess one.

## Audience behavior

Request scope:

```text
api://<client-id>/access_as_user
```

The verified v2 token used bare `<client-id>` in `aud`, with `ver: 2.0` and `scp: access_as_user`. The API therefore accepts both configured `api://<client-id>` and bare `<client-id>`. Keep issuer and tenant validation enabled through `https://login.microsoftonline.com/<tenant-id>/v2.0`.

## Scope creation order

Graph rejected creating a scope and preauthorizing it in one PATCH. Use two operations:

1. Add `requestedAccessTokenVersion` and `oauth2PermissionScopes`.
2. Add `preAuthorizedApplications` after the scope exists.

Azure CLI public client application ID: `04b07795-8ddb-461a-bbee-02f9e1bf7b46`.

## Token acquisition

```powershell
$scope = 'api://<client-id>/access_as_user'
$token = az account get-access-token `
  --scope $scope `
  --query accessToken `
  --output tsv `
  --only-show-errors
```

Never print `$token`. For diagnosis, decode only `aud`, `iss`, `ver`, `scp`, and `tid`.

## Failure decisions

| Symptom | Meaning | Action |
| --- | --- | --- |
| `consent_required` | Scope absent or client not consented | Create scope; preauthorize or obtain consent |
| Token issued, API 401 | Audience/authority mismatch | Compare non-sensitive claims with configuration |
| MSI call 403 | Missing role or propagation delay | Verify role at exact resource scope, then retry |
| PostgreSQL login failure | Administrator, token scope, or readiness issue | Verify all three; do not add password auth |

## Verification

Query deployed resources to prove local auth is disabled. Then prove `/health` is anonymous, business endpoints reject anonymous calls, delegated token calls pass validation, and semantic search reaches OpenAI and Search with managed identity.
