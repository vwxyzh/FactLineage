[CmdletBinding()]
param(
    [string] $ParametersFile = (Join-Path $PSScriptRoot 'deploy.parameters.json')
)

$ErrorActionPreference = 'Stop'
$config = Get-Content -Raw $ParametersFile | ConvertFrom-Json
$cloudRoot = Split-Path $PSScriptRoot -Parent
$repositoryRoot = Split-Path $cloudRoot -Parent
$dockerfile = Join-Path $cloudRoot 'src/FactLineage.Cloud.Api/Dockerfile'
$buildContext = $cloudRoot

function Assert-AzureCliSucceeded([string] $Operation) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

az group create `
    --name $config.resourceGroup `
    --location $config.location `
    --only-show-errors | Out-Null
Assert-AzureCliSucceeded 'Resource group creation'

az deployment group create `
    --name 'factlineage-foundation' `
    --resource-group $config.resourceGroup `
    --template-file (Join-Path $PSScriptRoot 'foundation.bicep') `
    --parameters `
        location=$($config.location) `
        managedIdentityName=$($config.managedIdentityName) `
        containerRegistryName=$($config.containerRegistryName) `
        containerAppsEnvironmentName=$($config.containerAppsEnvironmentName) `
        logAnalyticsWorkspaceName=$($config.logAnalyticsWorkspaceName) `
        postgresServerName=$($config.postgresServerName) `
        postgresDatabaseName=$($config.postgresDatabaseName) `
        searchServiceName=$($config.searchServiceName) `
        openAiAccountName=$($config.openAiAccountName) `
        embeddingDeploymentName=$($config.embeddingDeploymentName) `
        embeddingModelName=$($config.embeddingModelName) `
        embeddingModelVersion=$($config.embeddingModelVersion) `
        embeddingCapacity=$($config.embeddingCapacity) `
        embeddingSkuName=$($config.embeddingSkuName) `
    --only-show-errors | Out-Null
Assert-AzureCliSucceeded 'Foundation deployment'

az postgres flexible-server wait `
    --resource-group $config.resourceGroup `
    --name $config.postgresServerName `
    --custom "state == 'Ready'" `
    --only-show-errors
Assert-AzureCliSucceeded 'PostgreSQL readiness wait'

$identity = az identity show `
    --resource-group $config.resourceGroup `
    --name $config.managedIdentityName `
    --only-show-errors | ConvertFrom-Json
Assert-AzureCliSucceeded 'Managed identity lookup'

$tenantId = az account show --query tenantId --output tsv
Assert-AzureCliSucceeded 'Tenant lookup'

az deployment group create `
    --name 'postgres-entra-administrator' `
    --resource-group $config.resourceGroup `
    --template-file (Join-Path $PSScriptRoot 'postgres-administrator.bicep') `
    --parameters `
        postgresServerName=$($config.postgresServerName) `
        principalId=$($identity.principalId) `
        principalName=$($config.managedIdentityName) `
        tenantId=$tenantId `
    --only-show-errors | Out-Null
Assert-AzureCliSucceeded 'PostgreSQL Entra administrator deployment'

$imageName = "$($config.imageRepository):$($config.imageTag)"
az acr build `
    --registry $config.containerRegistryName `
    --image $imageName `
    --file $dockerfile `
    $buildContext `
    --only-show-errors
Assert-AzureCliSucceeded 'Container build'

$containerImage = "$($config.containerRegistryName).azurecr.io/$imageName"
az deployment group create `
    --name 'factlineage-application' `
    --resource-group $config.resourceGroup `
    --template-file (Join-Path $PSScriptRoot 'app.bicep') `
    --parameters `
        location=$($config.location) `
        managedIdentityName=$($config.managedIdentityName) `
        containerRegistryName=$($config.containerRegistryName) `
        containerAppsEnvironmentName=$($config.containerAppsEnvironmentName) `
        containerAppName=$($config.containerAppName) `
        postgresServerName=$($config.postgresServerName) `
        postgresDatabaseName=$($config.postgresDatabaseName) `
        searchServiceName=$($config.searchServiceName) `
        searchIndexName=$($config.searchIndexName) `
        searchSemanticConfigurationName=$($config.searchSemanticConfigurationName) `
        openAiAccountName=$($config.openAiAccountName) `
        embeddingDeploymentName=$($config.embeddingDeploymentName) `
        embeddingDimensions=$($config.embeddingDimensions) `
        apiAudience=$($config.apiAudience) `
        containerImage=$containerImage `
    --only-show-errors
Assert-AzureCliSucceeded 'Application deployment'

Set-Location $repositoryRoot