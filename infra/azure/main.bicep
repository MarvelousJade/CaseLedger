targetScope = 'resourceGroup'

@description('Short lowercase prefix used in Azure resource names.')
@minLength(2)
@maxLength(10)
param namePrefix string = 'caseledger'

@description('Logical deployment environment.')
@allowed([
  'dev'
  'staging'
  'prod'
])
param environmentName string = 'prod'

@description('Deployment phase. Foundation provisions infrastructure with publishers omitted; application creates revisions only after routing is enforced separately.')
@allowed([
  'foundation'
  'application'
])
param deploymentPhase string = 'foundation'

@description('Opaque rollout identifier added to both application templates so every supported application phase creates a fresh healthy revision after routing maintenance.')
@minLength(1)
@maxLength(64)
param deploymentRevision string = 'manual'

@description('Azure region for all regional resources.')
param location string = resourceGroup().location

@description('Public or authenticated container image for the API and SPA. Override only with a commit digest or immutable tag.')
param apiImage string = 'ghcr.io/marvelousjade/caseledger@sha256:ad62b224d5aceedcc89e9a8893e5bc5c5ac75ceb88a017f5dc441e66ac1f09bf'

@description('Public or authenticated container image for the audit worker. Override only with a commit digest or immutable tag.')
param workerImage string = 'ghcr.io/marvelousjade/caseledger-audit-worker@sha256:13309f1ce966140152b6b2810ff2d6aa8f8479b63f8220bd050cefc669194915'

@description('PostgreSQL administrator login name.')
@minLength(1)
@maxLength(63)
param postgresAdministratorLogin string = 'caseledgeradmin'

@description('PostgreSQL administrator password. Supply this through a secure deployment input.')
@secure()
@minLength(16)
@maxLength(128)
param postgresAdministratorPassword string

@description('Password for the seeded CaseLedger administrator. Supply this through a secure deployment input.')
@secure()
@maxLength(256)
param seedAdministratorPassword string = ''

@description('Password for the seeded CaseLedger analyst when demo login is enabled. Supply this through a secure deployment input.')
@secure()
@maxLength(256)
param seedAnalystPassword string = ''

@description('Required interactive authentication mode. Entra is the production default; Demo must be an explicit choice.')
@allowed([
  'Entra'
  'Demo'
  'DemoAndEntra'
])
param authenticationMode string

@description('Display the public CaseLedger demo credentials. Valid only for a deliberate Demo or DemoAndEntra deployment.')
param showDemoCredentials bool = false

@description('Microsoft Entra tenant ID used to validate issuer and tenant claims.')
param entraTenantId string = ''

@description('Microsoft Entra application (client) ID.')
param entraClientId string = ''

@description('Microsoft Entra application client secret. It is stored in Key Vault and exposed to the API only through a secret reference.')
@secure()
param entraClientSecret string = ''

@description('Allow an authenticated Entra identity without an existing mapping to become a local Analyst. Keep disabled unless this is intentional.')
param entraAutoProvisionAnalyst bool = false

@description('Immutable Entra object ID mapped once to the seeded CaseLedger administrator. Prefer this over tenant-wide auto-provisioning.')
param entraBootstrapAdministratorObjectId string = ''

@description('PostgreSQL database name.')
@minLength(1)
@maxLength(63)
param postgresDatabaseName string = 'caseledger'

@description('PostgreSQL major version.')
@allowed([
  '17'
  '18'
])
param postgresVersion string = '17'

@description('PostgreSQL compute SKU.')
param postgresSkuName string = 'Standard_D2ds_v5'

@description('PostgreSQL compute tier.')
@allowed([
  'Burstable'
  'GeneralPurpose'
  'MemoryOptimized'
])
param postgresSkuTier string = 'GeneralPurpose'

@description('PostgreSQL storage allocation in GiB.')
@minValue(32)
param postgresStorageSizeGb int = 64

@description('PostgreSQL backup retention in days.')
@minValue(7)
@maxValue(35)
param postgresBackupRetentionDays int = 14

@description('PostgreSQL high availability mode. Confirm regional support before enabling zone redundancy.')
@allowed([
  'Disabled'
  'SameZone'
  'ZoneRedundant'
])
param postgresHighAvailabilityMode string = 'Disabled'

@description('Minimum number of API replicas. Non-production environments may use zero so HTTP ingress can scale the API to zero when idle.')
@minValue(0)
param apiMinReplicas int = 1

@description('Maximum number of API replicas. Keep this at one until SignalR uses a distributed backplane and result consumption is coordinated across replicas.')
@minValue(1)
param apiMaxReplicas int = 1

@description('Maximum API request-outbox publish attempts. The minimum covers more than 75 minutes of exponential retry so the 60-minute deployment workflow cannot exhaust valid requests while Service Bus routing is reconciled.')
@minValue(25)
@maxValue(100)
param apiOutboxMaxAttempts int = 25

@description('Minimum number of worker replicas. Keep at least one during rehearsals and in production for durable result-outbox recovery. Zero is supported only with the configured Service Bus scale rule.')
@minValue(0)
param workerMinReplicas int = 1

@description('Maximum number of worker replicas.')
@minValue(1)
param workerMaxReplicas int = 3

@description('Service Bus tier. Standard is the lower-cost default; Premium raises the broker message-size ceiling and adds dedicated capacity.')
@allowed([
  'Standard'
  'Premium'
])
param serviceBusSkuName string = 'Standard'

@description('Premium messaging units. Used only when serviceBusSkuName is Premium.')
@allowed([
  1
  2
  4
  8
  16
])
param serviceBusPremiumMessagingUnits int = 1

@description('Maximum serialized audit message accepted by both API and worker. Standard deployments are automatically capped at 192 KiB, below the 256 KiB broker limit to leave room for broker metadata.')
@minValue(16384)
@maxValue(1048576)
param maximumAuditMessageBytes int = 196608

@description('Additional Azure resource tags.')
param additionalTags object = {}

var auditTopicName = 'caseledger-audit'
var workerRequestSubscriptionName = 'caseledger-audit-worker'
var apiResultSubscriptionName = 'caseledger-api-results'
var evidenceContainerName = 'evidence'
var dataProtectionContainerName = 'data-protection'
var deploymentSuffix = take(uniqueString(subscription().id, resourceGroup().id, namePrefix, environmentName), 6)
var baseName = toLower('${namePrefix}-${environmentName}-${deploymentSuffix}')
var compactBaseName = replace(baseName, '-', '')
var tags = union(additionalTags, {
  application: 'CaseLedger'
  environment: environmentName
  managedBy: 'Bicep'
})
var demoLoginEnabled = authenticationMode == 'Demo' || authenticationMode == 'DemoAndEntra'
var entraAuthenticationEnabled = authenticationMode == 'Entra' || authenticationMode == 'DemoAndEntra'
var deployApplications = deploymentPhase == 'application'
var storageAccountName = take('st${compactBaseName}', 24)
var keyVaultName = take('kv-${baseName}', 24)
var postgresServerName = '${baseName}-pg'
// Azure reserves namespace names ending in "-sb". Keep the purpose visible without using
// a provider-reserved suffix.
var serviceBusNamespaceName = '${baseName}-msg'
var apiContainerAppName = '${baseName}-api'
var workerContainerAppName = '${baseName}-worker'
var effectiveMaximumAuditMessageBytes = serviceBusSkuName == 'Standard'
  ? min(maximumAuditMessageBytes, 196608)
  : maximumAuditMessageBytes
var keyVaultSecretsUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4633458b-17de-408a-b874-0445c86b69e6'
)
var keyVaultCryptoUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '12338af0-0e69-4776-bea7-57ae8d297424'
)
var storageBlobDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${baseName}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.20.0.0/16'
      ]
    }
  }
}

resource containerAppsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: virtualNetwork
  name: 'container-apps'
  properties: {
    addressPrefix: '10.20.0.0/23'
    delegations: [
      {
        name: 'container-apps-environments'
        properties: {
          serviceName: 'Microsoft.App/environments'
        }
      }
    ]
  }
}

resource postgresSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: virtualNetwork
  name: 'postgres'
  properties: {
    addressPrefix: '10.20.4.0/24'
    delegations: [
      {
        name: 'postgres-flexible-server'
        properties: {
          serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
        }
      }
    ]
  }
}

resource postgresPrivateDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: '${baseName}.postgres.database.azure.com'
  location: 'global'
  tags: tags
}

resource postgresPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: postgresPrivateDnsZone
  name: 'caseledger-vnet-link'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-logs'
  location: location
  tags: tags
  properties: {
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${baseName}-cae'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: listKeys(logAnalytics.id, '2020-08-01').primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: containerAppsSubnet.id
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-api-id'
  location: location
  tags: tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-worker-id'
  location: location
  tags: tags
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  #disable-next-line BCP334
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    containerDeleteRetentionPolicy: {
      days: 14
      enabled: true
    }
    deleteRetentionPolicy: {
      days: 14
      enabled: true
    }
    isVersioningEnabled: true
  }
}

resource evidenceContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: evidenceContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: dataProtectionContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    enablePurgeProtection: true
    enableRbacAuthorization: true
    enableSoftDelete: true
    publicNetworkAccess: 'Enabled'
    softDeleteRetentionInDays: 90
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: keyVault
  name: 'data-protection'
  properties: {
    attributes: {
      enabled: true
    }
    keyOps: [
      'unwrapKey'
      'wrapKey'
    ]
    keySize: 2048
    kty: 'RSA'
  }
}

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = {
  name: postgresServerName
  location: location
  tags: tags
  identity: {
    type: 'None'
  }
  sku: {
    name: postgresSkuName
    tier: postgresSkuTier
  }
  properties: {
    administratorLogin: postgresAdministratorLogin
    administratorLoginPassword: postgresAdministratorPassword
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
    backup: {
      backupRetentionDays: postgresBackupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    createMode: 'Create'
    highAvailability: {
      mode: postgresHighAvailabilityMode
    }
    network: {
      delegatedSubnetResourceId: postgresSubnet.id
      privateDnsZoneArmResourceId: postgresPrivateDnsZone.id
      publicNetworkAccess: 'Disabled'
    }
    storage: {
      autoGrow: 'Enabled'
      storageSizeGB: postgresStorageSizeGb
      type: 'Premium_LRS'
    }
    version: postgresVersion
  }
  dependsOn: [
    postgresPrivateDnsLink
  ]
}

resource postgresDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: postgresServer
  name: postgresDatabaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusNamespaceName
  location: location
  tags: tags
  sku: serviceBusSkuName == 'Premium'
    ? {
        name: 'Premium'
        tier: 'Premium'
        capacity: serviceBusPremiumMessagingUnits
      }
    : {
        name: 'Standard'
        tier: 'Standard'
      }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    zoneRedundant: false
  }
}

resource apiPostgresKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(apiPostgresSecret.id, apiIdentity.id, keyVaultSecretsUserRoleId)
  scope: apiPostgresSecret
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

resource apiSeedAdministratorKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (demoLoginEnabled) {
  name: guid(seedAdministratorSecret.id, apiIdentity.id, keyVaultSecretsUserRoleId)
  scope: seedAdministratorSecret
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

resource apiSeedAnalystKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (demoLoginEnabled) {
  name: guid(seedAnalystSecret.id, apiIdentity.id, keyVaultSecretsUserRoleId)
  scope: seedAnalystSecret
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

resource apiEntraClientSecretKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (entraAuthenticationEnabled) {
  name: guid(entraClientSecretResource.id, apiIdentity.id, keyVaultSecretsUserRoleId)
  scope: entraClientSecretResource
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

resource workerPostgresKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(workerPostgresSecret.id, workerIdentity.id, keyVaultSecretsUserRoleId)
  scope: workerPostgresSecret
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

resource apiStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(evidenceContainer.id, apiIdentity.id, storageBlobDataContributorRoleId)
  scope: evidenceContainer
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource apiDataProtectionStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataProtectionContainer.id, apiIdentity.id, storageBlobDataContributorRoleId)
  scope: dataProtectionContainer
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource apiDataProtectionCryptoRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataProtectionKey.id, apiIdentity.id, keyVaultCryptoUserRoleId)
  scope: dataProtectionKey
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultCryptoUserRoleId
  }
}

var apiPostgresConnectionString = 'Host=${postgresServer.properties.fullyQualifiedDomainName};Port=5432;Database=${postgresDatabaseName};Username=${postgresAdministratorLogin};Password=${postgresAdministratorPassword};SSL Mode=VerifyFull;Trust Server Certificate=false;'
var workerPostgresConnectionUrl = 'postgresql://${uriComponent(postgresAdministratorLogin)}:${uriComponent(postgresAdministratorPassword)}@${postgresServer.properties.fullyQualifiedDomainName}:5432/${uriComponent(postgresDatabaseName)}?sslmode=verify-full'

resource apiPostgresSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'api-postgres-connection'
  properties: {
    contentType: 'application/x-npgsql-connection-string'
    value: apiPostgresConnectionString
  }
}

resource workerPostgresSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'worker-postgres-connection'
  properties: {
    contentType: 'application/x-postgresql-uri'
    value: workerPostgresConnectionUrl
  }
}

resource seedAdministratorSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (demoLoginEnabled) {
  parent: keyVault
  name: 'seed-administrator-password'
  properties: {
    contentType: 'text/plain'
    value: seedAdministratorPassword
  }
}

resource seedAnalystSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (demoLoginEnabled) {
  parent: keyVault
  name: 'seed-analyst-password'
  properties: {
    contentType: 'text/plain'
    value: seedAnalystPassword
  }
}

resource entraClientSecretResource 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (entraAuthenticationEnabled) {
  parent: keyVault
  name: 'entra-client-secret'
  properties: {
    contentType: 'text/plain'
    value: entraClientSecret
  }
}

var apiPostgresSecretUrl = '${keyVault.properties.vaultUri}secrets/${apiPostgresSecret.name}'
var workerPostgresSecretUrl = '${keyVault.properties.vaultUri}secrets/${workerPostgresSecret.name}'
var seedAdministratorSecretUrl = '${keyVault.properties.vaultUri}secrets/seed-administrator-password'
var seedAnalystSecretUrl = '${keyVault.properties.vaultUri}secrets/seed-analyst-password'
var entraClientSecretUrl = '${keyVault.properties.vaultUri}secrets/entra-client-secret'
var serviceBusFullyQualifiedNamespace = '${serviceBusNamespace.name}.servicebus.windows.net'
var dataProtectionKeyBlobUri = '${storageAccount.properties.primaryEndpoints.blob}${dataProtectionContainer.name}/keys.xml'
var dataProtectionKeyIdentifier = '${keyVault.properties.vaultUri}keys/${dataProtectionKey.name}'

resource apiContainerApp 'Microsoft.App/containerApps@2025-01-01' = if (deployApplications) {
  name: apiContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        stickySessions: {
          affinity: 'sticky'
        }
        targetPort: 8080
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
        transport: 'auto'
      }
      maxInactiveRevisions: 2
      secrets: concat([
          {
            identity: apiIdentity.id
            keyVaultUrl: apiPostgresSecretUrl
            name: 'postgres-connection'
          }
        ], demoLoginEnabled
        ? [
            {
              identity: apiIdentity.id
              keyVaultUrl: seedAdministratorSecretUrl
              name: 'seed-admin-password'
            }
            {
              identity: apiIdentity.id
              keyVaultUrl: seedAnalystSecretUrl
              name: 'seed-analyst-password'
            }
          ]
        : [], entraAuthenticationEnabled
        ? [
            {
              identity: apiIdentity.id
              keyVaultUrl: entraClientSecretUrl
              name: 'entra-client-secret'
            }
          ]
        : [])
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          env: concat([
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'ASPNETCORE_HTTP_PORTS'
              value: '8080'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'Authentication__DemoLoginEnabled'
              value: string(demoLoginEnabled)
            }
            {
              name: 'Authentication__ShowDemoCredentials'
              value: string(showDemoCredentials)
            }
            {
              name: 'Authentication__Entra__Enabled'
              value: string(entraAuthenticationEnabled)
            }
            {
              name: 'Authentication__Entra__TenantId'
              value: entraTenantId
            }
            {
              name: 'Authentication__Entra__ClientId'
              value: entraClientId
            }
            {
              name: 'Authentication__Entra__AutoProvisionAnalyst'
              value: string(entraAutoProvisionAnalyst)
            }
            {
              name: 'Authentication__Entra__BootstrapAdministratorObjectId'
              value: entraBootstrapAdministratorObjectId
            }
            {
              name: 'Database__Provider'
              value: 'PostgreSQL'
            }
            {
              name: 'ConnectionStrings__CaseLedger'
              secretRef: 'postgres-connection'
            }
            {
              name: 'EvidenceStorage__Provider'
              value: 'AzureBlob'
            }
            {
              name: 'EvidenceStorage__AzureBlob__AccountUri'
              value: storageAccount.properties.primaryEndpoints.blob
            }
            {
              name: 'EvidenceStorage__AzureBlob__ContainerName'
              value: evidenceContainer.name
            }
            {
              name: 'EvidenceStorage__AzureBlob__ManagedIdentityClientId'
              value: apiIdentity.properties.clientId
            }
            {
              name: 'Messaging__Enabled'
              value: 'true'
            }
            {
              name: 'CASELEDGER_DEPLOYMENT_REVISION'
              value: deploymentRevision
            }
            {
              name: 'Messaging__Provider'
              value: 'AzureServiceBus'
            }
            {
              name: 'Messaging__MaxAttempts'
              value: string(apiOutboxMaxAttempts)
            }
            {
              name: 'Messaging__AzureServiceBus__FullyQualifiedNamespace'
              value: serviceBusFullyQualifiedNamespace
            }
            {
              name: 'Messaging__AzureServiceBus__ManagedIdentityClientId'
              value: apiIdentity.properties.clientId
            }
            {
              name: 'Messaging__AzureServiceBus__TopicName'
              value: auditTopicName
            }
            {
              name: 'Messaging__AzureServiceBus__ResultSubscriptionName'
              value: apiResultSubscriptionName
            }
            {
              name: 'Messaging__MaximumMessageBytes'
              value: string(effectiveMaximumAuditMessageBytes)
            }
            {
              name: 'Webhook__Enabled'
              value: 'false'
            }
            {
              name: 'Security__RequireSecureCookies'
              value: 'true'
            }
            {
              name: 'DataProtection__Azure__Enabled'
              value: 'true'
            }
            {
              name: 'DataProtection__Azure__KeyBlobUri'
              value: dataProtectionKeyBlobUri
            }
            {
              name: 'DataProtection__Azure__KeyVaultKeyIdentifier'
              value: dataProtectionKeyIdentifier
            }
            {
              name: 'DataProtection__Azure__ManagedIdentityClientId'
              value: apiIdentity.properties.clientId
            }
            {
              name: 'OTEL_SERVICE_NAME'
              value: 'caseledger-api'
            }
          ], demoLoginEnabled
          ? [
              {
                name: 'Seed__AdminPassword'
                secretRef: 'seed-admin-password'
              }
              {
                name: 'Seed__AnalystPassword'
                secretRef: 'seed-analyst-password'
              }
            ]
          : [], entraAuthenticationEnabled
          ? [
              {
                name: 'Authentication__Entra__ClientSecret'
                secretRef: 'entra-client-secret'
              }
            ]
          : [])
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              failureThreshold: 60
              initialDelaySeconds: 5
              periodSeconds: 5
              successThreshold: 1
              timeoutSeconds: 3
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              failureThreshold: 3
              initialDelaySeconds: 30
              periodSeconds: 30
              successThreshold: 1
              timeoutSeconds: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              failureThreshold: 3
              initialDelaySeconds: 5
              periodSeconds: 10
              successThreshold: 1
              timeoutSeconds: 3
            }
          ]
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
        }
      ]
      scale: {
        minReplicas: apiMinReplicas
        maxReplicas: apiMaxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    apiPostgresKeyVaultRole
    apiSeedAdministratorKeyVaultRole
    apiSeedAnalystKeyVaultRole
    apiEntraClientSecretKeyVaultRole
    apiStorageRole
    apiDataProtectionStorageRole
    apiDataProtectionCryptoRole
    postgresDatabase
  ]
}

resource workerContainerApp 'Microsoft.App/containerApps@2025-01-01' = if (deployApplications) {
  name: workerContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      maxInactiveRevisions: 2
      secrets: [
        {
          identity: workerIdentity.id
          keyVaultUrl: workerPostgresSecretUrl
          name: 'postgres-connection'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'audit-worker'
          image: workerImage
          env: [
            {
              name: 'BROKER_PROVIDER'
              value: 'azure-service-bus'
            }
            {
              name: 'CASELEDGER_DEPLOYMENT_REVISION'
              value: deploymentRevision
            }
            {
              name: 'AZURE_SERVICE_BUS_FULLY_QUALIFIED_NAMESPACE'
              value: serviceBusFullyQualifiedNamespace
            }
            {
              name: 'AZURE_MANAGED_IDENTITY_CLIENT_ID'
              value: workerIdentity.properties.clientId
            }
            {
              name: 'AZURE_SERVICE_BUS_TOPIC'
              value: auditTopicName
            }
            {
              name: 'AZURE_SERVICE_BUS_REQUEST_SUBSCRIPTION'
              value: workerRequestSubscriptionName
            }
            {
              name: 'AUDIT_WORKER_DATABASE_URL'
              secretRef: 'postgres-connection'
            }
            {
              name: 'HEALTH_HOST'
              value: '0.0.0.0'
            }
            {
              name: 'HEALTH_PORT'
              value: '8081'
            }
            {
              name: 'MAX_MESSAGE_BYTES'
              value: string(effectiveMaximumAuditMessageBytes)
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health'
                port: 8081
                scheme: 'HTTP'
              }
              failureThreshold: 60
              initialDelaySeconds: 5
              periodSeconds: 5
              successThreshold: 1
              timeoutSeconds: 3
            }
            {
              type: 'Liveness'
              tcpSocket: {
                port: 8081
              }
              failureThreshold: 3
              initialDelaySeconds: 30
              periodSeconds: 30
              successThreshold: 1
              timeoutSeconds: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8081
                scheme: 'HTTP'
              }
              failureThreshold: 3
              initialDelaySeconds: 5
              periodSeconds: 10
              successThreshold: 1
              timeoutSeconds: 3
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: workerMinReplicas
        maxReplicas: workerMaxReplicas
        pollingInterval: 15
        cooldownPeriod: 300
        rules: [
          {
            name: 'servicebus-requests'
            custom: {
              type: 'azure-servicebus'
              identity: workerIdentity.id
              metadata: {
                messageCount: '5'
                namespace: serviceBusNamespace.name
                subscriptionName: workerRequestSubscriptionName
                topicName: auditTopicName
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    workerPostgresKeyVaultRole
    postgresDatabase
  ]
}

output apiUrl string = deployApplications ? 'https://${apiContainerApp!.properties.configuration.ingress.fqdn}' : ''
output entraRedirectUri string = deployApplications ? 'https://${apiContainerApp!.properties.configuration.ingress.fqdn}/signin-oidc' : ''
output apiContainerAppName string = '${baseName}-api'
output workerContainerAppName string = '${baseName}-worker'
output containerAppsEnvironmentName string = containerAppsEnvironment.name
output logAnalyticsWorkspaceId string = logAnalytics.id
output keyVaultUri string = keyVault.properties.vaultUri
output postgresHost string = postgresServer.properties.fullyQualifiedDomainName
output postgresDatabase string = postgresDatabase.name
output serviceBusNamespaceName string = serviceBusNamespace.name
output serviceBusNamespaceHost string = '${serviceBusNamespace.name}.servicebus.windows.net'
output serviceBusTopic string = auditTopicName
output workerRequestSubscription string = workerRequestSubscriptionName
output apiResultSubscription string = apiResultSubscriptionName
output evidenceBlobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output evidenceContainer string = evidenceContainer.name
output dataProtectionContainer string = dataProtectionContainer.name
output dataProtectionKeyIdentifier string = dataProtectionKeyIdentifier
output apiIdentityName string = apiIdentity.name
output apiIdentityClientId string = apiIdentity.properties.clientId
output workerIdentityName string = workerIdentity.name
output workerIdentityClientId string = workerIdentity.properties.clientId
output effectiveMaximumAuditMessageBytes int = effectiveMaximumAuditMessageBytes
output serviceBusTier string = serviceBusSkuName
