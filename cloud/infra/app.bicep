targetScope = 'resourceGroup'

param location string = resourceGroup().location
param managedIdentityName string
param containerRegistryName string
param containerAppsEnvironmentName string
param containerAppName string
param postgresServerName string
param postgresDatabaseName string
param searchServiceName string
param searchIndexName string
param searchSemanticConfigurationName string
param openAiAccountName string
param embeddingDeploymentName string
param embeddingDimensions int = 1536
param apiAudience string
param containerImage string
param tags object = {}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: managedIdentityName
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: containerRegistryName
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: containerAppsEnvironmentName
}

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          identity: identity.id
          server: registry.properties.loginServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          env: [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            {
              name: 'Cloud__ManagedIdentityClientId'
              value: identity.properties.clientId
            }
            {
              name: 'Cloud__TenantId'
              value: tenant().tenantId
            }
            {
              name: 'Cloud__ApiAudience'
              value: apiAudience
            }
            {
              name: 'Cloud__PostgreSql__Host'
              value: '${postgresServerName}.postgres.database.azure.com'
            }
            {
              name: 'Cloud__PostgreSql__Database'
              value: postgresDatabaseName
            }
            {
              name: 'Cloud__PostgreSql__User'
              value: identity.name
            }
            {
              name: 'Cloud__Search__Endpoint'
              value: 'https://${searchServiceName}.search.windows.net'
            }
            {
              name: 'Cloud__Search__IndexName'
              value: searchIndexName
            }
            {
              name: 'Cloud__Search__SemanticConfigurationName'
              value: searchSemanticConfigurationName
            }
            {
              name: 'Cloud__OpenAi__Endpoint'
              value: 'https://${openAiAccountName}.openai.azure.com/'
            }
            {
              name: 'Cloud__OpenAi__EmbeddingDeployment'
              value: embeddingDeploymentName
            }
            {
              name: 'Cloud__OpenAi__EmbeddingDimensions'
              value: string(embeddingDimensions)
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

output endpoint string = 'https://${app.properties.configuration.ingress.fqdn}'