targetScope = 'resourceGroup'

@description('Azure region for the analytics resources.')
param location string = resourceGroup().location

@minLength(3)
@maxLength(12)
param namePrefix string = 'clanalytics'

@allowed(['dev', 'staging', 'production'])
param environmentName string = 'dev'

param sqlAdministratorLogin string = 'caseledgeradmin'

@secure()
param sqlAdministratorPassword string

param tags object = {
  workload: 'caseledger-analytics'
  environment: environmentName
  provisionedBy: 'bicep'
}

var suffix = uniqueString(subscription().subscriptionId, resourceGroup().id)
var sqlServerName = toLower('${namePrefix}-${environmentName}-${suffix}')
var factoryName = toLower('${namePrefix}-${environmentName}-adf-${suffix}')
var vaultName = take(toLower('${namePrefix}-${environmentName}-kv-${suffix}'), 24)

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdministratorLogin
    administratorLoginPassword: sqlAdministratorPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
  }
}

resource analyticsDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'CaseLedgerAnalytics'
  location: location
  tags: tags
  sku: {
    name: 'S0'
    tier: 'Standard'
    capacity: 10
  }
  properties: {
    zoneRedundant: false
    readScale: 'Disabled'
  }
}

resource azureServicesFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

resource dataFactory 'Microsoft.DataFactory/factories@2018-06-01' = {
  name: factoryName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publicNetworkAccess: 'Enabled'
  }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, dataFactory.id, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    principalId: dataFactory.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
  }
}

output dataFactoryName string = dataFactory.name
output keyVaultUri string = keyVault.properties.vaultUri
output sqlServerFullyQualifiedDomainName string = sqlServer.properties.fullyQualifiedDomainName
output analyticsDatabaseName string = analyticsDatabase.name
