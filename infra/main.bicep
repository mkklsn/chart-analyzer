// Chart Analyzer: one always-on container, a free-offer Azure SQL database, and logs.

@description('Azure region. Not a US region: Binance blocks US locations.')
param location string = resourceGroup().location

@description('Region for the SQL server. Separate because some regions (e.g. North Europe) sometimes stop accepting new SQL servers.')
param sqlLocation string = 'uksouth'

@description('Name of the registry created by registry.bicep.')
param registryName string

@description('Full image reference, e.g. myregistry.azurecr.io/chart-analyzer:20261031120000')
param image string

param sqlAdminLogin string = 'chartadmin'

@secure()
param sqlAdminPassword string

@secure()
param telegramBotToken string

param telegramChatId string

@description('Time zone for times in alerts.')
param timeZone string = 'Europe/London'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

// The app pulls its image with this identity instead of a registry password.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-chartanalyzer'
  location: location
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, 'AcrPull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-chartanalyzer'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: json('0.1') // keeps log costs near zero
    }
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  // The region is part of the name: a failed attempt in another region keeps its name reserved.
  name: 'sql-chartanalyzer-${uniqueString(resourceGroup().id, sqlLocation)}'
  location: sqlLocation
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// Lets Azure services (the container app) reach the server. No other IPs are allowed.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Basic tier (~$5/month): always on. The free serverless offer pauses when idle, and each wake-up
// uses at least an hour of its monthly allowance, which runs out within days at our write rate.
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'chartanalyzer'
  location: sqlLocation
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    useFreeLimit: false
    maxSizeBytes: 2147483648 // 2 GB, the Basic maximum
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-chartanalyzer'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-chartanalyzer'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  dependsOn: [
    acrPull
  ]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
      secrets: [
        {
          name: 'telegram-bot-token'
          value: telegramBotToken
        }
        {
          name: 'sql-connection'
          value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;Connection Timeout=60'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'chart-analyzer'
          image: image
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
            { name: 'ConnectionStrings__ChartAnalyzer', secretRef: 'sql-connection' }
            { name: 'Telegram__BotToken', secretRef: 'telegram-bot-token' }
            { name: 'Telegram__ChatId', value: telegramChatId }
            { name: 'Analyzer__TimeZone', value: timeZone }
          ]
        }
      ]
      // Exactly one copy: two would send duplicate alerts and fight over the Telegram bot.
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}
